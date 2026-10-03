using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M610: opening a character by name.
///
/// <para>The browser picks a champion, a character and a skin; this is the part that makes that skin
/// readable and hands it to the preview the editor already has. Two ways in, because there are two states
/// the editor can be in:</para>
///
/// <para>With a project open the champion WAD is added as a read-only FALLBACK mount. That makes every
/// asset it holds readable without putting a single champion file into the project's own asset tree —
/// dropping Ahri into someone's Halloween map, or worse closing their project to look at a skin, is not
/// something a browse should ever do.</para>
///
/// <para>With nothing open there is no project to protect, so the champion WAD is simply loaded the way
/// any WAD is inspected.</para>
/// </summary>
public sealed partial class MainWindowViewModel : ICharacterBrowserHost
{
    /// <summary>
    /// M812: the ONE champion WAD the Character window has open - the one its next read needs - and the DATA/FINAL folder it
    /// came from. "Is it mounted?" is asked of the mounts (<see cref="IsMountedAsFallback"/>), not of this; this is what
    /// <see cref="BuildMounts"/> puts back. BuildMounts replaces the whole mount service and the champion WAD goes with the old
    /// one; this used to be a set that went on saying "mounted", so after ANY project change the next skin of that champion found
    /// nothing to read - not its textures, not its sibling skins, not a colour scan.
    ///
    /// <para>Only this one, and only while the window has it open: a rebuild runs ~600 ms after ANY change under the project
    /// root, on the UI thread, and every WAD it opens is a resolver pass over thousands of chunks. Every other champion mounts
    /// again, lazily, on its next <c>OpenSkin</c>. And only from the folder it came from: a champion WAD of another install
    /// would be served AHEAD of the new install's own copy (the first fallback that holds a hash answers), silently putting an
    /// old patch's files in the window.</para>
    /// </summary>
    private (string Wad, string Final, long OpenedAt)? _openChampionWad;

    /// <summary>How long after <c>OpenSkin</c> the skin counts as open even though its load has not reached the window yet (the
    /// window's card is set at the END of the load, a second or two on).</summary>
    private const long ChampionLoadGraceMilliseconds = 30_000;

    /// <summary>M812: the skins the champion WAD lists for the character that was opened last (<see cref="CharacterEntry.Skins"/>,
    /// a path scan - no bin parsed), so a colour scan can tell a sibling skin it could not read from one the character never had.</summary>
    private (string Folder, IReadOnlyList<int> Numbers)? _openedCharacterSkins;

    string? ICharacterBrowserHost.GameDirectory => Project.GameDirectory;
    IHashResolver? ICharacterBrowserHost.Resolver => _resolver;

    /// <summary>M643: opens the Character Editor window, whose left panel is the champion picker. The
    /// separate browser window of M610 still exists; nothing opens it any more.</summary>
    [RelayCommand]
    private void OpenCharacterBrowser()
    {
        EnsureCharacterBrowser();
        ShowMeshPreviewWindow?.Invoke();
    }

    private string? _browserGameDirectory;

    /// <summary>The picker embedded in the character window: created once, listed again when the game
    /// folder changes, because it lists whichever install the project points at.</summary>
    private void EnsureCharacterBrowser()
    {
        if (MeshPreview.Browser is null)
            MeshPreview.Browser = new CharacterBrowserViewModel(this, (category, message) => _log.Info(category, message));
        else if (!string.Equals(_browserGameDirectory, Project.GameDirectory, StringComparison.OrdinalIgnoreCase))
            MeshPreview.Browser.Reload();
        _browserGameDirectory = Project.GameDirectory;
    }

    void ICharacterBrowserHost.OpenSkin(ChampionPackage champion, CharacterEntry character, CharacterSkinInfo skin)
    {
        if (skin.MeshPath is not { Length: > 0 } meshPath)
        { _log.Warn("Character", $"{skin.CodeName} has no mesh."); return; }

        try
        {
            _openedCharacterSkins = (character.Name, character.Skins.Where(s => !s.IsRoot).Select(s => s.Number).ToArray());
            if (!MakeCharacterWadReadable(champion.WadPath)) return;

            // The mesh is addressed the same way every other asset is: by hash, resolved or hex (M592).
            ulong hash = BinTexturePath.HashOfReference(meshPath);
            if (!TryResolveEntry(hash, out var entry))
            { _log.Error("Character", $"{meshPath} is not in {Path.GetFileName(champion.WadPath)}."); return; }

            _log.Info("Character", $"{champion.Name} / {character.Name} / {skin.CodeName} — {Path.GetFileName(meshPath)}");
            // M728: the skin's OWN bin travels with the mesh. Every read below used to work the bin out from the
            // mesh's folder, and a chroma draws its base skin's mesh - Lillia's skin 49 opened as skin 46.
            _ = LoadMeshPreviewAsync(entry, skin.BinPath);
            TryLoadMaterialBin(entry, alsoRawBin: true, skinBin: skin.BinPath);
        }
        catch (Exception ex)
        {
            _log.Error("Character", ex.Message);
        }
    }

    /// <summary>M612: the action list for a loaded character — Q/W/E/R named from the champion record,
    /// plus attack, move, recall and emotes matched to this skin's clips.
    ///
    /// <para>Empty for anything that is not a character. A prop or a map mesh has no champion record, and
    /// four empty ability rows on a lamppost would be worse than no list at all.</para></summary>
    private IReadOnlyList<CharacterAction> BuildCharacterActions(
        WadAssetEntry skn, IReadOnlyList<Formats.Skeletons.AnimClipInfo>? allClips)
    {
        // M636: the arena needs the install and the readers this window owns; handed over on every
        // character load, because the resolver only exists once the hash database has been read.
        //
        // M725: ABOVE the early return, not below it. The arena is a property of the map and the install,
        // not of the subject, but it was configured only for subjects that resolved animation clips - so
        // loading a prop or a clipless skin left the arena card unconfigured, and whether you could walk
        // around a map depended on whether the thing you last clicked had animations.
        if (_resolver is { } resolver)
        {
            MeshPreview.ConfigureArena(new MeshPreviewViewModel.ArenaHost(
                Project.GameDirectory, resolver, ResolveBinName, ResolveWadPath));
            // M725: put the configured NVR backdrop back when an arena is unloaded.
            MeshPreview.ReapplyBackdrop = ApplyPreviewBackgroundAsync;
        }

        if (allClips is not { Count: > 0 } || !skn.IsResolved) return Array.Empty<CharacterAction>();

        try
        {
            var parts = skn.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            int ci = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
            if (ci < 0 || ci + 1 >= parts.Length) return Array.Empty<CharacterAction>();

            string character = parts[ci + 1];
            var spells = TryResolveEntry(HashAlgorithms.WadPath(ChampionRecord.PathFor(character)), out var record)
                ? ChampionRecord.SpellNames(ReadAsset(record.PathHash))
                : Array.Empty<string>();

            // M663: every clip, de-duplicated by clip NAME. It used to be the by-.anm-file view, on the
            // assumption that its values were this skin's clips exactly once - Riot points several clips
            // at one .anm file, so that view had already dropped the base graph's own Spell2.
            var actions = CharacterActions.Build(spells, allClips);

            // M631: and the spell records, so the composite can use the champion's own missile speeds and
            // cast timings instead of the constants it used to invent. Same bin the spell NAMES came from,
            // read once more rather than threaded through - it is a few hundred objects.
            if (TryResolveEntry(HashAlgorithms.WadPath(ChampionRecord.PathFor(character)), out var recordEntry))
            {
                byte[] recordBytes = ReadAsset(recordEntry.PathHash);
                var abilities = ChampionSpellData.Read(recordBytes, character);
                MeshPreview.SetAbilities(abilities);

                // M638: the basic attacks - clip, windup frame, hit, and the missile for a ranged champion.
                var attacks = ChampionSpellData.ReadAttacks(recordBytes, character);
                MeshPreview.SetAttacks(attacks);
                if (attacks.Count > 0)
                    _log.Info("Character", $"{character}: {attacks.Count(a => !a.IsCrit)} basic attack(s) in the cycle"
                                           + (attacks.Any(a => a.IsRanged) ? ", ranged" : ", melee")
                                           + $", {attacks.Count(a => a.IsCrit)} crit.");

                // M636: the champion's authored movement and attack numbers, for the arena.
                var stats = ChampionStatsReader.Read(recordBytes);
                MeshPreview.SetStats(stats);
                if (stats is not null)
                    _log.Info("Character", $"{character}: move speed {stats.MoveSpeed:0}, attack range {stats.AttackRange:0}, "
                                           + $"attack speed {stats.AttackSpeed:0.###}, HP {stats.BaseHealth:0}.");
                int missiles = abilities.Count(a => a.HasMissile);
                if (missiles > 0)
                    _log.Info("Character",
                        $"{character}: {missiles} ability slot(s) with an authored missile, "
                        + $"{abilities.Count(a => a.CastFrame > 0f || a.CastTimeSeconds > 0f)} with authored cast timing.");
            }
            else MeshPreview.SetAbilities(Array.Empty<AbilitySlot>());
            _log.Info("Character",
                $"{character}: {actions.Count(a => a.HasClip)}/{actions.Count} actions have an animation"
                + (spells.Count > 0 ? $", {spells.Count} abilities named from the record." : ", no champion record."));
            return actions;
        }
        catch (Exception ex)
        {
            // A character that will not describe itself must still preview.
            _log.Warn("Character", $"action list: {ex.Message}");
            return Array.Empty<CharacterAction>();
        }
    }

    /// <summary>M618: resolve this skin into a D3D11 scene, so the preview window can draw it with Riot's
    /// own compiled shaders instead of the OpenGL approximation.
    ///
    /// <para>Runs off the UI thread as part of loading the skin, because it decodes every texture the
    /// character references. Returns null for anything that is not a character with a skin bin — a prop
    /// has no materials to resolve, and the window falls back to GL rather than showing an empty frame.</para></summary>
    /// <param name="skinBin">M728: the skin bin the window was opened with. The mesh's folder decides only when
    /// nobody chose one - a chroma draws its base skin's mesh with a bin of its own.</param>
    private (Services.PreparedCharacterScene? Scene, string Status) BuildCharacterDx11Scene(
        WadAssetEntry skn, byte[]? binOverride = null, Formats.Materials.MaterialDriverState? driverState = null,
        string? skinBin = null)
    {
        if (!skn.IsResolved) return (null, "");

        // M620: opening the cache is this path's job too. It used to happen only while building a MAP
        // scene, so every character reported "no materials" for a reason that had nothing to do with it.
        if (OpenDx11ShaderCache(out var cacheError) is not { } cache)
            return (null, "Shader cache: " + (cacheError ?? "not readable"));

        try
        {
            string? binPath = Formats.Meta.SkinPaths.PreviewBinPath(skinBin, skn.Path);
            // M642: the character editor hands its EDITED bin in; the load path reads the shipped one.
            byte[]? bin = binOverride ?? (binPath is not null && TryResolveEntry(HashAlgorithms.WadPath(binPath), out var binEntry)
                ? ReadAsset(binEntry.PathHash)
                : null);

            var scene = Services.Dx11CharacterScene.Prepare(
                ReadAsset(skn.PathHash), bin, cache, ShaderPerms(),
                readAsset: h => { try { return ReadAsset(h); } catch { return null; } },
                resolveBinName: ResolveBinName,
                resolveWadPath: ResolveWadPath,
                fallbackShader: Services.Dx11CharacterScene.DefaultCharacterShader,
                driverState: driverState);   // M647: which of the skin's conditions to draw

            if (scene is null) return (null, "The mesh would not decode for D3D11.");

            // M623: to the console, in full. The status line has room for a count; the reason a submesh
            // did not resolve is a sentence, and it is the sentence that says what to do about it.
            foreach (string line in scene.Report.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                _log.Info("D3D11", line.TrimEnd());
            foreach (string why in scene.Failures) _log.Warn("D3D11", why);
            foreach (var slice in scene.Slices)
                _log.Info("D3D11", $"  {slice.Submesh} -> {slice.Material}"
                                   + $"  idx {slice.Start}+{slice.Count}"
                                   + $"  {slice.Textures.Count} texture(s)"
                                   + (slice.Hidden ? "  HIDDEN by initialSubmeshToHide" : "")
                                   + (slice.UsedFallbackShader ? "  (stand-in shader)" : ""));
            return (scene, scene.Slices.Count > 0
                ? $"{scene.Slices.Count} of {scene.SubmeshCount} submesh(es) resolved"
                : "No materials resolved - " + (scene.Failures.Count > 0 ? scene.Failures[0] : "the skin bin was not found"));
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>M676: the D3D11 scene for a placed prop - Riot's shaders, the skin's textures, parameters and
    /// drivers at rest - prepared exactly as <see cref="BuildCharacterDx11Scene"/> prepares a champion for
    /// the character window. Null when the mesh carries no bytes (an added mesh) or the shader cache is not
    /// readable; the D3D11 prop driver then keeps its diffuse-only draw for that mesh.</summary>
    internal Services.PreparedCharacterScene? PreparePropDx11Scene(PropMesh mesh)
    {
        if (mesh.SknBytes is null) return null;
        if (OpenDx11ShaderCache(out _) is not { } cache) return null;
        // M807: the preparation itself is PropMeshBuilder's, which the Content Browser's map thumbnails call as well.
        return Services.PropMeshBuilder.PrepareDx11Scene(mesh, cache, ShaderPerms(),
            readAsset: h => { try { return ReadAsset(h); } catch { return null; } },
            resolveBinName: ResolveBinName,
            resolveWadPath: ResolveWadPath);
    }

    /// <summary>M680: the map's lightgrid, read once per map on first ask. The bin names it
    /// (MapBakeProperties.lightGridFileName, 180 of 180 shipped maps); a map without one, or with a file
    /// that is not a modern grid, leaves the props on the neutral cube and says so once.</summary>
    private (string? Map, Formats.Lighting.LightGridFile? Grid) _propLightGrid;

    private Formats.Lighting.LightGridFile? PropLightGrid()
    {
        string? mapPath = _currentMapEntry?.Path;
        if (mapPath is null) return null;
        if (_propLightGrid.Map == mapPath) return _propLightGrid.Grid;

        Formats.Lighting.LightGridFile? grid = null;
        try
        {
            if (TryResolveMaterialsBin(mapPath, out var binEntry)
                && Formats.MapGeo.MapBakeProperties.Read(ReadAsset(binEntry.PathHash)) is { } bake
                && bake.File.Length > 0)
            {
                var bytes = ReadAssetByPath(bake.File);
                if (bytes is not null && Formats.Lighting.LightGridFile.LooksLikeLightGrid(bytes))
                {
                    grid = Formats.Lighting.LightGridFile.Read(bytes);
                    _log.Info("Props", $"Lightgrid {grid.Width}x{grid.Height} over {grid.WorldSizeX:0}x{grid.WorldSizeZ:0} "
                                     + $"(cube scale {grid.FullBrightScale * 4f:0.##}, self-illum {grid.CharacterFullBrightIntensity:0.##}): "
                                     + "placed mobs and props take their ambient cube from it.");
                }
                else _log.Info("Props", $"{bake.File}: not readable as a lightgrid - props keep the neutral ambient cube.");
            }
            else _log.Info("Props", "This map declares no lightgrid - props keep the neutral ambient cube.");
        }
        catch (Exception ex) { _log.Warn("Props", "Lightgrid not read: " + ex.Message); }
        _propLightGrid = (mapPath, grid);
        return grid;
    }

    /// <summary>M680: what the map lights a placement with - see <see cref="PropLighting"/>. Null when the
    /// map has no lightgrid.</summary>
    internal PropLighting? PropLightingAt(System.Numerics.Vector3 world)
    {
        var grid = PropLightGrid();
        if (grid is null) return null;
        return new PropLighting(Formats.Lighting.LightGridFile.ToLightGridColors(grid.SampleAmbient(world)), grid.LightGridScale);
    }

    /// <summary>Make a champion WAD readable without disturbing whatever is already open. True when the
    /// assets can now be read.</summary>
    private bool MakeCharacterWadReadable(string wadPath)
    {
        if (_mounts is null)
        {
            // Nothing open: inspection mode is exactly what this is for.
            LoadWad(wadPath);
            return _archive is not null;
        }

        // M812: ask the mounts, not a note that remembers having asked (see _openChampionWad)
        if (IsMountedAsFallback(_mounts, wadPath)) { NoteCharacterWadOpened(wadPath); return true; }

        try
        {
            var champion = new WadMount(WadArchive.Open(wadPath, _resolver),
                AssetSourceKind.RiotReference, editable: false, name: Path.GetFileName(wadPath));
            // M807: the fallback list is enumerated by a thumbnail draw on its own thread; a draw that overlaps this add is not kept
            NoteMapThumbnailInputsChanged();
            _mounts.AddFallback(champion);
            NoteMapThumbnailInputsChanged();
            NoteCharacterWadOpened(wadPath);
            _log.Info("Character", $"Mounted {Path.GetFileName(wadPath)} as a read-only reference.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("Character", $"{Path.GetFileName(wadPath)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>M812: is this WAD already one of the live service's mounts - a project's explicit reference, or a fallback?</summary>
    private static bool IsMountedAsFallback(AssetMountService mounts, string wadPath)
    {
        string wanted = Path.GetFullPath(wadPath);
        return mounts.Fallback.Concat(mounts.Mounts).OfType<WadMount>()
            .Any(m => string.Equals(Path.GetFullPath(m.Location), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>M812: this champion WAD is the one the Character window has open now, and this is the install it came from.</summary>
    private void NoteCharacterWadOpened(string wadPath)
    {
        string? final = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        _openChampionWad = final is not null && IsUnder(wadPath, final) ? (wadPath, final, Environment.TickCount64) : null;
    }

    /// <summary>Is a champion skin on screen in the Character window - or one being loaded into it?</summary>
    private bool CharacterWindowHasChampionOpen(long openedAt) =>
        MeshPreview.HasChromaCard || Environment.TickCount64 - openedAt < ChampionLoadGraceMilliseconds;

    private static bool IsUnder(string path, string folder)
    {
        string root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// M812: <see cref="BuildMounts"/> has just made a new mount service, and the champion WAD the Character window has open was
    /// mounted on the old one. Put it back, so a window that is open keeps reading its champion - its textures, its animations,
    /// its other skins - through a rebuild instead of going quietly empty.
    ///
    /// <para><b>One WAD, from the current install.</b> Forgotten instead - and mounted again by the next <c>OpenSkin</c> - when the
    /// window has moved on to something that is not a champion, or the project's game folder is no longer the install the WAD came
    /// from (<c>OpenProjectAt</c>, <c>SetGameFolder</c> and <c>ApplyProjectSettings</c> all rebuild; a project on the same install
    /// keeps its champion, one on another install does not get the old install's file ahead of its own).</para>
    ///
    /// <para><b>Opened with the resolver</b>, as <see cref="MakeCharacterWadReadable"/> opens it, and not like a game fallback
    /// (which has none). Reads by hash alone would not need it - but the next <c>OpenSkin</c> of this champion finds the mount
    /// already there and takes the mesh's ENTRY from it, and with an unresolved entry <c>TryPairSkeleton</c> finds no skeleton,
    /// <c>FindAnimations</c> no animations and <c>BuildCharacterActions</c> no abilities (each returns nothing when
    /// <c>!entry.IsResolved</c>): the skin would open bare. Measured on the real host, one champion WAD opens in about 3 ms with the
    /// resolver and 1 ms without, so the resolver costs a rebuild about 2 ms.</para>
    /// </summary>
    private void RemountCharacterWads()
    {
        if (_mounts is null || _openChampionWad is not { } open) return;
        if (!CharacterWindowHasChampionOpen(open.OpenedAt)) { _openChampionWad = null; return; }

        string? final = GameReferenceLibrary.FindFinalDirectory(Project.GameDirectory);
        if (final is null || !SamePath(final, open.Final) || !IsUnder(open.Wad, final))
        {
            _openChampionWad = null;
            _log.Info("Character", $"{Path.GetFileName(open.Wad)} came from another game install than this project's; it mounts again if its skin is opened here.");
            return;
        }

        try
        {
            if (!IsMountedAsFallback(_mounts, open.Wad))
                _mounts.AddFallback(new WadMount(WadArchive.Open(open.Wad, _resolver),
                    AssetSourceKind.RiotReference, editable: false, name: Path.GetFileName(open.Wad)));
        }
        catch (Exception ex)
        {
            // gone: dropped, and mounted again if its skin is opened
            _openChampionWad = null;
            _log.Warn("Character", $"{Path.GetFileName(open.Wad)} could not be mounted again after the project changed: {ex.Message}");
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
