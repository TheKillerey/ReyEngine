using Avalonia.Threading;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M642: the character window's material editor - the host half.
///
/// <para>There are two material editors now, and this is the one wiring both get. The map's lives in the
/// inspector and previews into the map viewport; the character's lives in the character window and
/// previews into THAT window, on whichever renderer it is showing. Everything else - thumbnails, the
/// shader catalogue, undo, the override save, the bin-issues window - is the same code with the editor
/// passed in, because the alternative was a second copy of nineteen hook assignments that would have
/// drifted the first time one of them changed.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The skin the character window is showing, so a material edit can rebuild the D3D11 scene
    /// for it. Null while the window shows a prop or a legacy map.</summary>
    private WadAssetEntry? _previewSkn;

    /// <summary>Only the newest rebuild lands. A burst of edits starts a burst of off-thread scene builds,
    /// and without this the SLOWEST one would win rather than the LAST one.</summary>
    private int _characterSceneRebuild;
    private bool _warnedCharacterBinMismatch;

    /// <summary>Both editors, for anything that must reach every one of them - the shader catalogue, the
    /// environment list, an asset removal.</summary>
    private IEnumerable<MaterialEditorViewModel> MaterialEditors
    {
        get { yield return MaterialEditor; yield return MeshPreview.MaterialEditor; }
    }

    private void WireMaterialEditor(MaterialEditorViewModel editor, Action applyToViewport, Func<Task> saveOverride)
    {
        editor.UndoService = UndoService;
        editor.CopyHandler = Dialogs.CopyAsync;
        editor.TextureExists = TextureExistsByPath;
        editor.LoadThumbnail = LoadThumbnailByPath;
        // M368: the same schema source the particle editor uses, so both panels agree by construction.
        editor.DeclaredProperties = h => Meta.PropertiesOf(h);
        editor.ClassName = h => Meta.TryGetName(h, out var n) ? n : null;
        editor.LoadTextureRaw = LoadTextureByPath;   // M351k: the material ball samples raw RGBA
        editor.OpenTexture = OpenTextureByPath;
        editor.ReplaceTextureAsset = slot => ReplaceTextureForSlot(slot, applyToViewport);
        editor.ApplyToViewport = applyToViewport;
        editor.Edited = ScheduleAutoSave;   // M505: arm auto-save on the EDIT, not on the preview
        editor.AskMacroSupport = CachedMacroSupport;   // M506: inline permutation verdicts
        editor.Warn = m => _log.Warn("Material", m);                 // M533
        editor.AskMacroFixes = SuggestMacroFixes;                    // M533
        editor.SaveOverride = saveOverride;
        editor.RequestCatalog = LoadShaderCatalogAsync;   // M103
        editor.RequestCommonShaderSetup = LoadCommonShaderSetupAsync;
        editor.OpenIssues = () => OpenMaterialBinIssues(editor);   // M125
    }

    /// <summary>The catalogue goes to every editor, and the environment that produced it is mirrored
    /// onto the ones that did not ask - silently, or each mirror would request the catalogue again.</summary>
    private void SetShaderCatalog(string environment, ShaderCatalog? catalog)
    {
        foreach (var editor in MaterialEditors)
        {
            editor.SetEnvironmentSilently(environment);
            editor.SetCatalog(catalog);
        }
    }

    private Task SaveCharacterMaterialOverride() =>
        SaveMaterialOverrideFor(MeshPreview.MaterialEditor, ApplyCharacterMaterialsToPreview);

    /// <summary>The window is about to show something that is not a champion skin. The skin's materials
    /// leave with the skin - the same rule the inspector applies when another asset is selected.</summary>
    private void ForgetPreviewSkin()
    {
        _previewSkn = null;
        MeshPreview.MaterialEditor.Clear();
        MeshPreview.ShowMaterials(false);
    }

    /// <summary>The hash of the skin bin that belongs to the previewed .skn, or 0.</summary>
    private ulong PreviewSkinBinHash =>
        _previewSkn is { IsResolved: true } skn && SkinPaths.BinPathForSkn(skn.Path) is { } binPath
            ? HashAlgorithms.WadPath(binPath)
            : 0;

    /// <summary>
    /// The character editor's live preview. The edited bin is serialised and resolved again exactly the
    /// way the load path resolved the shipped one, on BOTH renderers: the GL viewport takes the
    /// re-resolved diffuse per submesh, and the D3D11 scene is rebuilt from the edited bytes off the UI
    /// thread. The rebuild is what was missing before - with D3D11 on, a material edit changed nothing.
    /// </summary>
    private void ApplyCharacterMaterialsToPreview()
    {
        var editor = MeshPreview.MaterialEditor;
        var bytes = editor.Serialize();
        if (bytes is null) return;
        // M505: arm auto-save BEFORE any early return below - the edit is unsaved state whether or not
        // the window can show it.
        if (editor.IsDirty) ScheduleAutoSave();

        if (_previewSkn is not { } skn || MeshPreview.Mesh is not { } mesh) return;
        if (editor.BinEntry is not { } binEntry || binEntry.PathHash != PreviewSkinBinHash)
        {
            // A skin bin picked in the content browser while the window shows another champion. The
            // edits are real and will save; there is just nothing here to preview them on.
            if (!_warnedCharacterBinMismatch)
            {
                _warnedCharacterBinMismatch = true;
                _log.Info("Material", $"{editor.BinEntry?.DisplayName ?? "The edited bin"} is not the skin the character window is showing - edits are kept, not previewed.");
            }
            return;
        }
        _warnedCharacterBinMismatch = false;

        try
        {
            var resolved = ChampionMaterialResolver.Resolve(bytes, ResolveBinName, ResolveWadPath);
            MeshPreview.Textures = ResolveSubmeshDiffuse(mesh, resolved);
        }
        catch (Exception ex)
        {
            _log.Warn("Material", "Preview (GL): " + ex.Message);
        }

        RebuildCharacterDx11Scene(bytes);
    }

    /// <summary>
    /// M647: the D3D11 half of the character preview, rebuilt for the current bin AND the current driver
    /// state. Split out of <see cref="ApplyCharacterMaterialsToPreview"/> because flipping a state switch
    /// is not an edit: nothing to re-resolve for GL, nothing to auto-save, only a scene to build again
    /// with different parameter values.
    /// </summary>
    private void RebuildCharacterDx11Scene(byte[]? binBytes = null)
    {
        if (_previewSkn is not { } skn) return;
        byte[]? bytes = binBytes ?? MeshPreview.MaterialEditor.Serialize();
        var state = MeshPreview.DriverState;
        int token = ++_characterSceneRebuild;
        _ = Task.Run(() => BuildCharacterDx11Scene(skn, bytes, state)).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _log.Warn("Material", "Preview (D3D11): " + (t.Exception?.GetBaseException().Message ?? "failed"));
                return;
            }
            var (scene, status) = t.Result;
            Dispatcher.UIThread.Post(() =>
            {
                if (token != _characterSceneRebuild) return;   // a newer edit already rebuilt
                if (scene is not null) MeshPreview.SetDx11Scene(scene, status);
                else _log.Warn("Material", "Preview (D3D11): " + status);
            });
        });
    }

    /// <summary>The diffuse per submesh from a resolved skin bin, or null when the bin names none. The
    /// load path and the live preview share this so they cannot resolve the same bin two ways.
    ///
    /// <para>Deliberately NOT <c>BuildSubmeshTextures</c>: that one also publishes the secondary layers
    /// into the MAIN viewport's state, which belongs to whatever map is open there.</para></summary>
    private IReadOnlyList<TextureImage?>? ResolveSubmeshDiffuse(MeshAsset mesh, ChampionMaterialResolver.Result resolved)
    {
        if (!resolved.HasAny) return null;
        var cache = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        var result = new TextureImage?[mesh.SubMeshes.Count];
        for (int i = 0; i < mesh.SubMeshes.Count; i++)
        {
            var p = resolved.For(mesh.SubMeshes[i].Material);
            if (string.IsNullOrEmpty(p)) continue;
            if (!cache.TryGetValue(p, out var img)) cache[p] = img = LoadTextureByPath(p);
            result[i] = img;
        }
        return result;
    }
}
