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

    /// <summary>
    /// M703: give a submesh a material of its own.
    ///
    /// <para>A character material is optional - Riot's scenery characters ship none, and neither does
    /// anything the Character Creator writes - and a shader can only be changed on a material that
    /// exists. This authors one from a skinned shader, seeded with the texture that submesh already
    /// draws with so the model does not change appearance, points the submesh at it, saves, and selects
    /// it in the editor, where its shader and parameters are then ordinary edits.</para>
    /// </summary>
    private async Task AddCharacterSubmeshMaterialAsync(string submeshName)
    {
        var editor = MeshPreview.MaterialEditor;
        if (editor.BinEntry is not { } binEntry) { _log.Warn("Material", "No skin bin is open, so there is nothing to add a material to."); return; }
        if (editor.Serialize() is not { } bytes) { _log.Warn("Material", "The open skin bin could not be read."); return; }

        // the shader comes from the catalogue, so it has to be there
        if (editor.Catalog is null && editor.SelectedShaderEnvironment is { } environment)
            await LoadShaderCatalogAsync(environment);
        if (Formats.Characters.CharacterMaterialBinder.PickShader(editor.Catalog) is not { } shader)
        { _log.Error("Material", "No shader catalogue is loaded - pick a game environment in the Materials tab first."); return; }

        // M706: the dictionary first, then the bin's own path - a character somebody made is not in the
        // dictionary, and those are the ones with no material to begin with.
        if (Formats.Characters.CharacterMaterialBinder.SkinObjectPath(bytes, ResolveBinName,
                binEntry.IsResolved ? binEntry.Path : null) is not { } skinPath)
        {
            _log.Error("Material", "This bin's skin object could not be named: the hash database does not know it, "
                + "and its path does not match the file it is in. Nothing to name a material after.");
            return;
        }

        // what the submesh draws with today: its own texture override, else the skin's default
        string? diffuse = Formats.Characters.CharacterMaterialBinder.Overrides(bytes)
            .FirstOrDefault(o => o.Submesh.Equals(submeshName, StringComparison.OrdinalIgnoreCase))?.Texture;
        diffuse ??= Formats.Meshes.SkinMeshExtractor.Extract(bytes, ResolveWadPath)?.DefaultTexture;

        var updated = Formats.Characters.CharacterMaterialBinder.AddMaterial(
            bytes, skinPath, submeshName, shader, diffuse, out var error, out var materialPath);
        if (updated is null) { _log.Error("Material", error ?? "The material could not be added."); return; }

        // reload the editor on the new bytes, keeping the baseline it was opened against so the save
        // still rebases onto whatever the project holds
        var doc = Formats.Materials.MaterialDocument.Parse(updated, ResolveBinName, ResolveWadPath);
        var baseline = editor.BaseBytes;
        editor.Load(doc, binEntry, baseline);
        editor.IsDirty = true;   // the file on disk does not have this yet
        MeshPreview.RefreshOutliner();
        editor.SelectedMaterial = editor.Materials.FirstOrDefault(m =>
            m.Name.Equals(materialPath, StringComparison.OrdinalIgnoreCase))
            ?? editor.Materials.FirstOrDefault(m => m.Name.EndsWith(materialPath[(materialPath.LastIndexOf('/') + 1)..], StringComparison.OrdinalIgnoreCase));
        ApplyCharacterMaterialsToPreview();
        await SaveCharacterMaterialOverride();
        _log.Success("Material", $"'{submeshName}' now has its own material ({materialPath}) on {shader.Name}. "
            + "Change its shader and parameters here - the list offers the shaders a character can be drawn with.");
    }

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
            MeshPreview.Materials = ResolveSubmeshMaterials(mesh, resolved);   // M664: and its render state
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
    /// <summary>
    /// M664: the RENDER STATE per submesh from the same resolved bin — alpha mode, blend factors,
    /// two-sidedness, UV transform.
    ///
    /// <para>The preview window had none of this. <c>Show</c> clears Materials and only the legacy-map
    /// viewer ever set it again, so a champion drew with every submesh opaque and Riot's blend modes
    /// ignored. Aatrox's Wings are <c>Shaders/SkinnedMesh/Scrolling_ColorDodge_Masked</c> with
    /// blendEnable set and a diffuse whose alpha is 100% opaque — the transparency is entirely in the
    /// blend mode — so the wing drew as its raw texture, and Riot painted the parts meant to dodge away
    /// black.</para>
    /// </summary>
    private IReadOnlyList<ReyEngine.Rendering.ViewportMeshRenderer.SubmeshMaterial>? ResolveSubmeshMaterials(
        MeshAsset mesh, ChampionMaterialResolver.Result resolved)
    {
        if (!resolved.HasAny) return null;
        var mats = new ReyEngine.Rendering.ViewportMeshRenderer.SubmeshMaterial[mesh.SubMeshes.Count];
        for (int i = 0; i < mesh.SubMeshes.Count; i++)
            mats[i] = ToSubmeshMaterial(resolved.Profile(mesh.SubMeshes[i].Material))
                // M664: a champion blends but still occludes - see SubmeshMaterial.BlendWritesDepth.
                with { BlendWritesDepth = true };
        return mats;
    }

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
