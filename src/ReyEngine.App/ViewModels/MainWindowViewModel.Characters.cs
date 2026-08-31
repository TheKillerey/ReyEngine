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

    [RelayCommand]
    private void OpenCharacterBrowser()
    {
        if (PromptOwner is null) { _log.Error("Character", "The window is not ready yet."); return; }
        Views.CharacterBrowserWindow.Show(PromptOwner, this, (category, message) => _log.Info(category, message));
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
        WadAssetEntry skn, IReadOnlyDictionary<string, Formats.Skeletons.AnimClipInfo>? clipsByAnm)
    {
        if (clipsByAnm is not { Count: > 0 } || !skn.IsResolved) return Array.Empty<CharacterAction>();

        try
        {
            var parts = skn.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            int ci = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
            if (ci < 0 || ci + 1 >= parts.Length) return Array.Empty<CharacterAction>();

            string character = parts[ci + 1];
            var spells = TryResolveEntry(HashAlgorithms.WadPath(ChampionRecord.PathFor(character)), out var record)
                ? ChampionRecord.SpellNames(ReadAsset(record.PathHash))
                : Array.Empty<string>();

            // clipsByAnm is keyed by .anm file name, so its values are this skin's clips exactly once.
            var actions = CharacterActions.Build(spells, clipsByAnm.Values.ToList());
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
    private (Services.PreparedCharacterScene? Scene, string Status) BuildCharacterDx11Scene(WadAssetEntry skn)
    {
        if (!skn.IsResolved) return (null, "");

        // M620: opening the cache is this path's job too. It used to happen only while building a MAP
        // scene, so every character reported "no materials" for a reason that had nothing to do with it.
        if (OpenDx11ShaderCache(out var cacheError) is not { } cache)
            return (null, "Shader cache: " + (cacheError ?? "not readable"));

        try
        {
            string? binPath = Formats.Meta.SkinPaths.BinPathForSkn(skn.Path);
            byte[]? bin = binPath is not null && TryResolveEntry(HashAlgorithms.WadPath(binPath), out var binEntry)
                ? ReadAsset(binEntry.PathHash)
                : null;

            var scene = Services.Dx11CharacterScene.Prepare(
                ReadAsset(skn.PathHash), bin, cache, ShaderPerms(),
                readAsset: h => { try { return ReadAsset(h); } catch { return null; } },
                resolveBinName: ResolveBinName,
                resolveWadPath: ResolveWadPath);

            if (scene is null) return (null, "The mesh would not decode for D3D11.");
            return (scene, scene.Slices.Count > 0
                ? $"{scene.Slices.Count} of {scene.SubmeshCount} submesh(es) resolved"
                : "No materials resolved - " + (scene.Failures.Count > 0 ? scene.Failures[0] : "the skin bin was not found"));
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
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
