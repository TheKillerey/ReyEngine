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
    /// <summary>Champion WADs already mounted as fallbacks this session, so browsing back and forth does
    /// not mount the same archive again and again.</summary>
    private readonly HashSet<string> _characterWads = new(StringComparer.OrdinalIgnoreCase);

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
            if (!MakeCharacterWadReadable(champion.WadPath)) return;

            // The mesh is addressed the same way every other asset is: by hash, resolved or hex (M592).
            ulong hash = BinTexturePath.HashOfReference(meshPath);
            if (!TryResolveEntry(hash, out var entry))
            { _log.Error("Character", $"{meshPath} is not in {Path.GetFileName(champion.WadPath)}."); return; }

            _log.Info("Character", $"{champion.Name} / {character.Name} / {skin.CodeName} — {Path.GetFileName(meshPath)}");
            _ = LoadMeshPreviewAsync(entry);
            TryLoadMaterialBin(entry, alsoRawBin: true);
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
        if (allClips is not { Count: > 0 } || !skn.IsResolved) return Array.Empty<CharacterAction>();

        // M636: the arena needs the install and the readers this window owns; handed over on every
        // character load, because the resolver only exists once the hash database has been read.
        if (_resolver is { } resolver)
            MeshPreview.ConfigureArena(new MeshPreviewViewModel.ArenaHost(
                Project.GameDirectory, resolver, ResolveBinName, ResolveWadPath));

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
    private (Services.PreparedCharacterScene? Scene, string Status) BuildCharacterDx11Scene(
        WadAssetEntry skn, byte[]? binOverride = null, Formats.Materials.MaterialDriverState? driverState = null)
    {
        if (!skn.IsResolved) return (null, "");

        // M620: opening the cache is this path's job too. It used to happen only while building a MAP
        // scene, so every character reported "no materials" for a reason that had nothing to do with it.
        if (OpenDx11ShaderCache(out var cacheError) is not { } cache)
            return (null, "Shader cache: " + (cacheError ?? "not readable"));

        try
        {
            string? binPath = Formats.Meta.SkinPaths.BinPathForSkn(skn.Path);
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
        try
        {
            return Services.Dx11CharacterScene.Prepare(mesh.SknBytes, mesh.SkinBinBytes, cache, ShaderPerms(),
                readAsset: h => { try { return ReadAsset(h); } catch { return null; } },
                resolveBinName: ResolveBinName,
                resolveWadPath: ResolveWadPath,
                fallbackShader: Services.Dx11CharacterScene.DefaultCharacterShader);
        }
        catch { return null; }
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

        if (!_characterWads.Add(wadPath)) return true;      // already mounted this session

        try
        {
            _mounts.AddFallback(new WadMount(WadArchive.Open(wadPath, _resolver),
                AssetSourceKind.RiotReference, editable: false, name: Path.GetFileName(wadPath)));
            _log.Info("Character", $"Mounted {Path.GetFileName(wadPath)} as a read-only reference.");
            return true;
        }
        catch (Exception ex)
        {
            _characterWads.Remove(wadPath);
            _log.Error("Character", $"{Path.GetFileName(wadPath)}: {ex.Message}");
            return false;
        }
    }
}
