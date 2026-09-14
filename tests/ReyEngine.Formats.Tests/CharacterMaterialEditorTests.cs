using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M642: the character window edits the skin's materials, and the edit reaches BOTH renderers.
///
/// <para>Before this the skin bin loaded into the main window's inspector - a window away from the
/// character, and one whose material editor the next map-mesh click overwrote - and with D3D11 on an
/// edit there changed nothing on screen, because the D3D11 scene was built once at load. What is pinned
/// here: the second editor exists and is wired like the first; a skin bin parses as a champion document
/// (which is what routes it); and an edited slot changes what the GL resolver and the D3D11 scene
/// builder both resolve for the same skin bytes.</para>
/// </summary>
public sealed class CharacterMaterialEditorTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string Champions = Final + @"\Champions";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private sealed record Fixture(byte[] Skn, byte[] SkinBin, ShaderCacheReader Cache, WadArchive Archive, HashDatabase Database) : IDisposable
    {
        public string? BinName(uint h) => Database.TryGetBinName(h, out var n) ? n : null;
        public string? WadPath(ulong h) => Database.TryGetPath(h, out var p) ? p : null;
        public byte[]? Read(ulong h) => Archive.TryGetEntry(h, out _) ? Archive.Extract(h) : null;
        public void Dispose() { Cache.Dispose(); Archive.Dispose(); }
    }

    private static Fixture? Ahri()
    {
        string wad = Path.Combine(Champions, "Ahri.wad.client");
        if (!Directory.Exists(Final) || !File.Exists(wad) || Database.Value is not { } database) return null;
        var resolver = new WadPathResolver(database);
        var archive = WadArchive.Open(wad, resolver);
        var cache = ShaderCacheReader.Open(Final, resolver, out _);
        byte[]? skn = archive.TryGetEntry(HashAlgorithms.WadPath("assets/characters/ahri/skins/base/ahri_base.skn"), out _)
            ? archive.Extract(HashAlgorithms.WadPath("assets/characters/ahri/skins/base/ahri_base.skn")) : null;
        byte[]? bin = archive.TryGetEntry(HashAlgorithms.WadPath("data/characters/ahri/skins/skin0.bin"), out _)
            ? archive.Extract(HashAlgorithms.WadPath("data/characters/ahri/skins/skin0.bin")) : null;
        if (cache is null || skn is null || bin is null) { archive.Dispose(); cache?.Dispose(); return null; }
        return new Fixture(skn, bin, cache, archive, database);
    }

    // ===================================================== the second editor

    [Fact]
    public void TheCharacterWindowOwnsASecondEditorWiredLikeTheInspectors()
    {
        var vm = new MainWindowViewModel();
        var inspector = vm.MaterialEditor;
        var character = vm.MeshPreview.MaterialEditor;
        Assert.NotSame(inspector, character);

        // One wiring, two editors: every host hook the inspector's editor has, the character's has too.
        foreach (var editor in new[] { inspector, character })
        {
            Assert.NotNull(editor.UndoService);
            Assert.NotNull(editor.ApplyToViewport);
            Assert.NotNull(editor.SaveOverride);
            Assert.NotNull(editor.LoadThumbnail);
            Assert.NotNull(editor.TextureExists);
            Assert.NotNull(editor.RequestCatalog);
            Assert.NotNull(editor.OpenIssues);
            Assert.NotNull(editor.Edited);
            Assert.NotNull(editor.ReplaceTextureAsset);
        }
        Assert.Same(inspector.UndoService, character.UndoService);
        Assert.Equal(inspector.ShaderEnvironments, character.ShaderEnvironments);

        // ...but they preview into different places.
        Assert.NotEqual(inspector.ApplyToViewport!.Method.Name, character.ApplyToViewport!.Method.Name);

        // The window is a Model Preview until a skin's materials arrive, then a Character Editor.
        Assert.False(vm.MeshPreview.HasMaterialEditor);
        Assert.Contains("Model Preview", vm.MeshPreview.WindowTitle);
        vm.MeshPreview.ShowMaterials(true);
        Assert.Contains("Character Editor", vm.MeshPreview.WindowTitle);
        Assert.Equal(MeshPreviewViewModel.MaterialTab, vm.MeshPreview.PanelTab);
        vm.MeshPreview.ShowMaterials(false);
        Assert.NotEqual(MeshPreviewViewModel.MaterialTab, vm.MeshPreview.PanelTab);   // a hidden tab is never the selected one
    }

    [Fact]
    public void ASkinBinIsAChampionDocument()
    {
        // The routing key: a champion skin's materials go to the character window, a map's stay in the
        // inspector. That decision is the document's Kind, so this is the fact the routing rests on.
        if (Ahri() is not { } f) return;
        using (f)
        {
            var doc = MaterialDocument.Parse(f.SkinBin, f.BinName, f.WadPath);
            Assert.Equal(MaterialSourceKind.ChampionSkin, doc.Kind);
            Assert.True(doc.Materials.Count > 0);
        }
    }

    // ===================================================== an edit reaches both renderers

    [Fact]
    public void AnEditedDiffuseChangesWhatBothRenderersResolve()
    {
        if (Ahri() is not { } f) return;
        using (f)
        {
            var doc = MaterialDocument.Parse(f.SkinBin, f.BinName, f.WadPath);
            var editor = new MaterialEditorViewModel();
            Assert.True(f.Archive.TryGetEntry(HashAlgorithms.WadPath("data/characters/ahri/skins/skin0.bin"), out var binEntry));
            editor.Load(doc, binEntry, f.SkinBin);
            Assert.True(editor.HasMaterials);

            var mesh = SkinnedMeshDecoder.Decode(f.Skn);
            var meshNames = mesh.SubMeshes.Select(s => s.Material).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // The material is picked by its AUTHORED binding, not by the path it resolves to: several
            // materials of one skin share a diffuse, so "this submesh resolves to X" does not say which
            // material it goes through. A materialOverride entry names its submeshes; the default
            // material owns whatever no override claims.
            var bound = editor.Materials.Where(m => m.Slots.Any(s => s.IsDiffuse) && m.Model.Submeshes.Any(meshNames.Contains)).ToList();
            var claimed = editor.Materials.SelectMany(m => m.Model.Submeshes).ToHashSet(StringComparer.OrdinalIgnoreCase);
            MaterialBindingViewModel material;
            List<string> expectedSubmeshes;
            if (bound.Count > 0)
            {
                material = bound[0];
                expectedSubmeshes = material.Model.Submeshes.Where(meshNames.Contains).ToList();
            }
            else
            {
                var defaults = editor.Materials.Where(m => m.Model.IsDefault && m.Slots.Any(s => s.IsDiffuse)).ToList();
                Assert.Single(defaults);   // the resolver takes the FIRST default with a diffuse; two would make the test ambiguous
                material = defaults[0];
                expectedSubmeshes = meshNames.Where(n => !claimed.Contains(n)).ToList();
            }
            var slot = material.Slots.First(s => s.IsDiffuse);
            string original = slot.EditedPath;

            // Another texture that exists in the same WAD - a different skin's diffuse.
            string replacement = f.Archive.Entries
                .Where(e => e.IsResolved && e.Path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                            && e.Path.Contains("/skins/skin", StringComparison.OrdinalIgnoreCase)
                            && e.Path.Contains("_tx_cm", StringComparison.OrdinalIgnoreCase)
                            && !e.Path.Equals(original, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal).First();

            slot.EditedPath = replacement;
            slot.ApplyCommand.Execute(null);
            Assert.True(editor.IsDirty);
            var edited = editor.Serialize();
            Assert.NotNull(edited);

            // GL: the same resolver the load path uses now names the replacement for that material's submeshes.
            var resolvedBefore = ChampionMaterialResolver.Resolve(f.SkinBin, f.BinName, f.WadPath);
            var resolvedAfter = ChampionMaterialResolver.Resolve(edited!, f.BinName, f.WadPath);
            Assert.NotEmpty(expectedSubmeshes);
            foreach (var sub in expectedSubmeshes)
            {
                Assert.Equal(original, resolvedBefore.For(sub), ignoreCase: true);
                Assert.Equal(replacement, resolvedAfter.For(sub), ignoreCase: true);
            }

            // D3D11: the scene built from the edited bytes binds the replacement on that submesh's slice.
            var scene = Dx11CharacterScene.Prepare(f.Skn, edited, f.Cache, perms: null, f.Read, f.BinName, f.WadPath,
                fallbackShader: Dx11CharacterScene.DefaultCharacterShader);
            Assert.NotNull(scene);
            string want = replacement.ToLowerInvariant();
            var slice = scene!.Slices.FirstOrDefault(s => string.Equals(s.Submesh, expectedSubmeshes[0], StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(slice);
            Assert.Contains(slice!.Textures, t => t.Key == want);
            Assert.DoesNotContain(slice.Textures, t => t.Key == original.ToLowerInvariant());
            Assert.True(scene.Textures.ContainsKey(want), "the replacement was not decoded for the scene");
        }
    }

    // ===================================================== the wiring is where it says it is

    [Fact]
    public void TheEditorIsOneControlHostedTwice()
    {
        var view = Source("src", "ReyEngine.App", "Views", "MaterialEditorView.axaml");
        var inspector = Source("src", "ReyEngine.App", "Views", "InspectorView.axaml");
        var window = Source("src", "ReyEngine.App", "Views", "MeshPreviewWindow.axaml");
        if (view is null || inspector is null || window is null) return;

        Assert.Contains("x:DataType=\"vm:MaterialEditorViewModel\"", view);
        Assert.Contains("x:CompileBindings=\"True\"", view);
        Assert.DoesNotContain("Binding MaterialEditor.", view);   // the prefix that tied it to one editor
        Assert.Contains("SelectedItem=\"{Binding SelectedMaterial}\"", view);

        Assert.Contains("<views:MaterialEditorView DataContext=\"{Binding MaterialEditor}\" />", inspector);
        Assert.DoesNotContain("MaterialEditor.FilteredMaterials", inspector);

        Assert.Contains("<views:MaterialEditorView DataContext=\"{Binding MaterialEditor}\" />", window);
        Assert.Contains("SelectedIndex=\"{Binding PanelTab}\"", window);
        Assert.Contains("Title=\"{Binding WindowTitle}\"", window);
        Assert.Contains("x:CompileBindings=\"True\"", window);   // XAML bindings are runtime otherwise (memory)
        Assert.Contains("IsVisible=\"{Binding HasMaterialEditor}\"", window);
    }

    [Fact]
    public void AnEditRebuildsTheD3D11SceneAndTheSkinRoutesToTheCharacterWindow()
    {
        var host = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.CharacterMaterials.cs");
        var main = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (host is null || main is null) return;

        // Both renderers, from the SAME edited bytes. M647 moved the D3D11 half into
        // RebuildCharacterDx11Scene, which the state switch shares, and the call now carries the
        // situation to draw as well as the bytes.
        Assert.Contains("MeshPreview.Textures = ResolveSubmeshDiffuse(mesh, resolved);", host);
        Assert.Contains("RebuildCharacterDx11Scene(bytes);", host);
        // M728: and which skin - the bin the window was opened with, not the one in the mesh's folder
        Assert.Contains("BuildCharacterDx11Scene(skn, bytes, state, skinBin)", host);
        Assert.Contains("MeshPreview.SetDx11Scene(scene, status)", host);

        // The routing, and that the inspector keeps its map document when a skin arrives.
        Assert.Contains("matDoc.Kind == MaterialSourceKind.ChampionSkin", main);
        Assert.Contains("MeshPreview.MaterialEditor.Load(matDoc, binEntry, bytes);", main);
        Assert.DoesNotContain("MaterialEditor.SetCatalog(", main);   // the catalogue reaches every editor
    }
}
