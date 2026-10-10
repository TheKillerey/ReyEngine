using System.Numerics;
using System.Reflection;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Undo;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.Graph;
using ReyEngine.Formats.Shaders;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M830: the Material Graph edits Riot's own materials through the real view models: a Map11 SRX_Blend* material (a
/// colour parameter, a texture and a switch, then the real save into a scratch folder project, a reload and an undo), and a
/// champion skin's material in the Character window's editor. Riot's files, the user's projects and the settings are never
/// written: the project is a scratch folder under the temp folder.
///
/// <para>No-ops (not failures) when the game install or the hash dictionary is absent, the convention of the other real-data tests.</para>
/// </summary>
public sealed class MaterialGraphEditRealDataTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private const string Final = Game + @"\DATA\FINAL";
    private const string Map11Wad = Final + @"\Maps\Shipping\Map11.wad.client";

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reyengine-m830-" + Guid.NewGuid().ToString("N"));

    public MaterialGraphEditRealDataTests(ITestOutputHelper output) { _output = output; Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly Lazy<HashDatabase?> Database = new(() => { try { return new HashSyncService().LoadLocal(_ => { }); } catch { return null; } });
    private static string? Name(uint h) => Database.Value is { } db && db.TryGetBinName(h, out var n) ? n : null;
    private static string? Path_(ulong h) => Database.Value is { } db && db.TryGetPath(h, out var p) ? p : null;
    private static bool HaveMap => File.Exists(Map11Wad) && Database.Value is not null;
    private static bool HaveChampion(string c) => File.Exists(Path.Combine(Final, "Champions", c + ".wad.client")) && Database.Value is not null;

    private static object? Call(MainWindowViewModel vm, string name, params object?[] args)
    {
        try { return typeof(MainWindowViewModel).GetMethod(name, Private)!.Invoke(vm, args); }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }

    private static byte[] ReadAsset(MainWindowViewModel vm, ulong hash) => (byte[])Call(vm, "ReadAsset", hash)!;

    private sealed class ShaderRig : IDisposable
    {
        public ShaderCacheReader Cache { get; }
        public ShaderPermutationIndex? Perms { get; }
        public ShaderRig()
        {
            var resolver = new WadPathResolver(Database.Value!);
            Cache = ShaderCacheReader.Open(Final, resolver, out _)!;
            try { Perms = new ShaderPermutationIndex(Final, h => resolver.TryGetPath(h, out var p) ? p : null); } catch { Perms = null; }
        }
        public void Dispose() => Cache.Dispose();
    }

    private (MainWindowViewModel Vm, ReyProject Project) OpenScratchProject(string name = "scratch")
    {
        string root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        var project = ReyProjectService.OpenFolder(root);
        project.GameDirectory = Game;
        project.ProjectVersion = 2;
        ReyProjectService.Save(project, project.ProjectFilePath!);
        return (Attach(project), project);
    }

    private static MainWindowViewModel Attach(ReyProject project)
    {
        var vm = new MainWindowViewModel { Project = project };
        vm.Settings.AutoSaveEdits = false;   // the machine's setting must not start a DispatcherTimer in a test
        Call(vm, "BuildMounts");
        return vm;
    }

    // ================================================================================================ Map11

    /// <summary>The first Map11 materials.bin material that has a colour parameter with a value, a texture and an authored
    /// switch - and the shader the permutation of which a switch toggle changes, when one does.</summary>
    private sealed record Pick(string BinPath, string Material, string ColourParam, string Sampler, string Switch);

    private static Pick? FindMap11Pick(MaterialDocument doc, string binPath)
    {
        foreach (var m in doc.Materials)
        {
            if (!(m.RenderShader ?? "").Contains("SRX_Blend", StringComparison.OrdinalIgnoreCase)) continue;
            var colour = m.Parameters.FirstOrDefault(p => !p.IsValueOmitted && p.IsEditable && p.TypeName == "Vector4"
                && (p.Name.Contains("Color", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Tint", StringComparison.OrdinalIgnoreCase)) && p.TryGetColor(out _));
            var slot = m.Slots.FirstOrDefault(s => s.Path.Length > 0 && !s.Path.StartsWith("0x"));   // since 16.17 a texture is a chunk link: one the dictionary names reads as its path
            var sw = m.AllSwitches.FirstOrDefault();
            if (colour is null || slot is null || sw is null || m.IsLinked) continue;
            return new Pick(binPath, m.Name, colour.Name, slot.SamplerName, sw.Name);
        }
        return null;
    }

    private static (byte[] Bytes, string Path, Pick Pick)? Map11Bin()
    {
        using var map = WadArchive.Open(Map11Wad, new WadPathResolver(Database.Value!));
        foreach (var e in map.Entries.Where(e => e.Path != null && e.Path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase) && e.Path.Contains("map11/", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] bytes;
            MaterialDocument doc;
            try { bytes = map.Extract(e.PathHash); doc = MaterialDocument.Parse(bytes, Name, Path_); } catch { continue; }
            if (FindMap11Pick(doc, e.Path) is { } pick) return (bytes, e.Path, pick);
        }
        return null;
    }

    private static string Perm(MaterialGraph g, string label) =>
        g.ShaderNode!.Details.FirstOrDefault(d => d.Label == label).Value ?? "";

    [Fact]
    public async Task AMap11SrxMaterial_ColourTextureAndSwitchAreEditedInTheGraph_SavedByTheEditorsOwnSave_ReloadedAndUndone()
    {
        if (!HaveMap) return;
        var foundBin = Map11Bin();
        Assert.True(foundBin is not null, "no Map11 SRX_Blend* material with a colour, a named texture and a switch was found");
        var found = foundBin!.Value;
        var (original, binPath, pick) = found;
        _output.WriteLine($"{binPath}: {pick}");
        using var shaders = new ShaderRig();

        // ---- a scratch folder project holding the project copy of the bin (what Copy To Project makes)
        var (vm, project) = OpenScratchProject("map11");
        Assert.True((bool)Call(vm, "MakeCharacterWadReadable", Map11Wad)!);
        ulong hash = HashAlgorithms.WadPath(binPath);
        object?[] resolve = { hash, null };
        Assert.True((bool)typeof(MainWindowViewModel).GetMethod("TryResolveEntry", Private)!.Invoke(vm, resolve)!);
        var riotEntry = (WadAssetEntry)resolve[1]!;
        riotEntry.Path = binPath;                                 // the test host has no hash dictionary wired into the view model: name the entry by the path the archive gave
        riotEntry.IsResolved = true;
        object?[] place = { riotEntry, original, "" };
        Assert.True((bool)Call(vm, "TryPlaceInProjectFolder", place)!,
            $"the project copy was not placed (folder project {project.IsFolderProject}, resolved {riotEntry.IsResolved}, root {project.RootPath})");
        Call(vm, "BuildMounts");
        Assert.True((bool)typeof(MainWindowViewModel).GetMethod("TryResolveEntry", Private)!.Invoke(vm, resolve)!);
        var entry = (WadAssetEntry)resolve[1]!;
        entry.Path = binPath;
        entry.IsResolved = true;
        string file = Path.Combine(project.RootPath!, ((string)place[2]!).Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(file), file);

        byte[] served = ReadAsset(vm, hash);
        var editor = vm.MaterialEditor;
        editor.LoadThumbnail = _ => null;
        editor.LoadTextureRaw = null;
        editor.Load(MaterialDocument.Parse(served, Name, Path_), entry, served);
        byte[] baseline = editor.Serialize()!;                    // the document as it serialises untouched
        var material = editor.Materials.Single(m => m.Name == pick.Material);
        var graph = new MaterialGraphViewModel(null);
        graph.ShowMaterial(material.Model, shaders.Cache, shaders.Perms, editor);
        Assert.True(graph.CanEdit);
        Assert.StartsWith("SRX_Blend", graph.Graph!.ShaderNode!.Title);

        // ---- a colour parameter
        var colourNode = graph.Graph.Nodes.First(n => n.Kind == GraphNodeKind.Color && n.Source == pick.ColourParam);
        graph.SelectedNodeId = colourNode.Id;
        var colourRow = Assert.IsType<GraphParamEditRow>(Assert.Single(graph.EditRows));
        string colourBefore = material.Model.Parameters.First(p => p.Name == pick.ColourParam).CurrentText;
        Assert.True(colourRow.HasPicker);
        colourRow.Hex = "#FF8000";
        colourRow.CommitHexCommand.Execute(null);
        string colourAfter = material.Model.Parameters.First(p => p.Name == pick.ColourParam).CurrentText;
        _output.WriteLine($"{pick.ColourParam}: {colourBefore}  ->  {colourAfter}");
        Assert.NotEqual(colourBefore, colourAfter);
        Assert.Equal(colourAfter, material.Parameters.First(p => p.Name == pick.ColourParam).EditedText);   // the Material tab's row
        Assert.Equal(1f, graph.Graph.Find(colourNode.Id)!.Swatch!.Value.X, 3);
        Assert.Equal(128 / 255f, graph.Graph.Find(colourNode.Id)!.Swatch!.Value.Y, 3);
        Assert.Equal(colourNode.Id, graph.SelectedNodeId);

        // ---- a texture: another texture of the same bin (resolvable, so the thumbnail path is real)
        var texNode = graph.Graph.Nodes.First(n => n.Kind == GraphNodeKind.Texture && n.Source == pick.Sampler && n.State != GraphNodeState.ShaderDefault);
        string texBefore = texNode.TexturePath;
        string texOther = material.Model.Slots.Select(s => s.Path).Concat(editor.Materials.SelectMany(m => m.Model.Slots).Select(s => s.Path))
            .First(p => p.Length > 0 && !p.StartsWith("0x") && !p.Equals(texBefore, StringComparison.OrdinalIgnoreCase));
        graph.SelectedNodeId = texNode.Id;
        var texRow = Assert.IsType<GraphTextureEditRow>(Assert.Single(graph.EditRows));
        texRow.Pending = texOther;
        texRow.CommitPathCommand.Execute(null);
        Assert.Equal(texOther, material.Model.Slots.First(s => s.SamplerName == pick.Sampler).Path);
        Assert.Equal(texOther, graph.Graph.Find(texNode.Id)!.TexturePath);
        Assert.Equal(HashAlgorithms.WadPath(texOther.ToLowerInvariant()), graph.Graph.Find(texNode.Id)!.TextureChunk);
        _output.WriteLine($"{pick.Sampler}: {texBefore}  ->  {texOther}");

        // ---- a switch
        var swNode = graph.Graph.Nodes.First(n => n.Kind == GraphNodeKind.Switch && n.Source == pick.Switch && n.State != GraphNodeState.ShaderDefault);
        graph.SelectedNodeId = swNode.Id;
        var swRow = Assert.IsType<GraphSwitchEditRow>(Assert.Single(graph.EditRows));
        bool swBefore = swRow.Switch.IsOn;
        string permBefore = Perm(graph.Graph, "Pixel permutation");
        swRow.Switch.IsOn = !swBefore;
        Assert.Equal(!swBefore, material.Model.AllSwitches.First(s => s.Name == pick.Switch).On);
        Assert.Equal(!swBefore ? "ON" : "OFF", graph.Graph.Find(swNode.Id)!.Outputs[0].Detail);
        _output.WriteLine($"{pick.Switch}: {(swBefore ? "ON" : "OFF")} -> {(!swBefore ? "ON" : "OFF")}; pixel permutation {permBefore} -> {Perm(graph.Graph, "Pixel permutation")}");
        Assert.Equal(swNode.Id, graph.SelectedNodeId);

        Assert.True(editor.IsDirty);
        Assert.True(graph.ShowDirty);

        // ---- Save: the editor's own save writes the project's file in place
        var saveLog = new List<string>();
        ((ReyEngine.Core.Diagnostics.Logger)typeof(MainWindowViewModel).GetField("_log", Private)!.GetValue(vm)!).Logged += e => { lock (saveLog) saveLog.Add($"{e.Level} {e.Category}: {e.Message}"); };
        await graph.SaveCommand.ExecuteAsync(null);
        foreach (var line in saveLog) _output.WriteLine(line);
        _output.WriteLine("same material view model after the save: " + ReferenceEquals(editor.Materials.Single(m => m.Name == pick.Material), material) + ", editor dirty " + editor.IsDirty);
        Assert.Equal(editor.IsDirty, graph.IsEditorDirty);        // the graph shows the editor's flag, whatever the save leaves it at
        byte[] saved = File.ReadAllBytes(file);
        Assert.NotEqual(original, saved);
        var reloaded = MaterialDocument.Parse(saved, Name, Path_);
        var m2 = reloaded.Materials.Single(m => m.Name == pick.Material);
        Assert.Equal(colourAfter, m2.Parameters.First(p => p.Name == pick.ColourParam).CurrentText);
        Assert.Equal(texOther, m2.Slots.First(s => s.SamplerName == pick.Sampler).Path);
        Assert.Equal(!swBefore, m2.AllSwitches.First(s => s.Name == pick.Switch).On);

        // nothing else in the bin moved: every other material is what it was
        var before = MaterialDocument.Parse(original, Name, Path_);
        foreach (var m in before.Materials.Where(m => m.Name != pick.Material))
        {
            var other = reloaded.Materials.Single(x => x.Name == m.Name);
            Assert.Equal(m.Parameters.Select(p => p.CurrentText), other.Parameters.Select(p => p.CurrentText));
            Assert.Equal(m.Slots.Select(s => s.Path), other.Slots.Select(s => s.Path));
            Assert.Equal(m.AllSwitches.Select(s => s.On), other.AllSwitches.Select(s => s.On));
        }

        // a second editor opened on the project's file (a reload) shows the saved values, in the graph and in the Material tab
        var editor2 = new MaterialEditorViewModel { UndoService = new UndoRedoService() };
        editor2.Load(MaterialDocument.Parse(ReadAsset(vm, hash), Name, Path_), entry, saved);
        var material2 = editor2.Materials.Single(m => m.Name == pick.Material);
        using var graph2 = new MaterialGraphViewModel(null);
        graph2.ShowMaterial(material2.Model, shaders.Cache, shaders.Perms, editor2);
        Assert.Equal(texOther, graph2.Graph!.Nodes.First(n => n.Kind == GraphNodeKind.Texture && n.Source == pick.Sampler && n.State != GraphNodeState.ShaderDefault).TexturePath);
        Assert.Equal(colourAfter, material2.Parameters.First(p => p.Name == pick.ColourParam).EditedText);

        // ---- Undo (Ctrl+Z) takes every edit back, in the reverse order, in the same document
        graph.UndoCommand.Execute(null);                         // switch
        graph.UndoCommand.Execute(null);                         // texture
        graph.UndoCommand.Execute(null);                         // colour
        Assert.Equal(swBefore, material.Model.AllSwitches.First(s => s.Name == pick.Switch).On);
        Assert.Equal(texBefore, material.Model.Slots.First(s => s.SamplerName == pick.Sampler).Path);
        Assert.Equal(colourBefore, material.Model.Parameters.First(p => p.Name == pick.ColourParam).CurrentText);
        Assert.Equal(colourBefore, material.Parameters.First(p => p.Name == pick.ColourParam).EditedText);
        Assert.Equal(baseline, editor.Serialize());              // byte-identical to the document before the first edit
        graph.Dispose();
        graph2.Dispose();
    }

    /// <summary>A switch that selects another compiled pixel permutation rebuilds the graph for it (its inputs, stats and code), keeps
    /// the selected node, and a toggle back restores the first permutation exactly.</summary>
    [Fact]
    public void ASwitchThatChangesThePermutationRebuildsTheGraphForItAndKeepsTheSelection()
    {
        if (!HaveMap) return;
        using var shaders = new ShaderRig();
        using var map = WadArchive.Open(Map11Wad, new WadPathResolver(Database.Value!));
        int tried = 0, changed = 0;
        foreach (var e in map.Entries.Where(e => e.Path != null && e.Path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase) && e.Path.Contains("map11/", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] bytes;
            MaterialDocument doc;
            try { bytes = map.Extract(e.PathHash); doc = MaterialDocument.Parse(bytes, Name, Path_); } catch { continue; }
            var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService() };
            editor.Load(doc, new WadAssetEntry { Path = e.Path, IsResolved = true, PathHash = e.PathHash }, bytes);
            foreach (var m in editor.Materials.Where(m => !m.IsLinked && m.Switches.Count > 0 && !string.IsNullOrEmpty(m.Model.RenderShader)).Take(40))
            {
                using var graph = new MaterialGraphViewModel(null);
                graph.ShowMaterial(m.Model, shaders.Cache, shaders.Perms, editor);
                if (graph.Graph?.ShaderNode is null || Perm(graph.Graph, "Pixel permutation").Length == 0) continue;
                foreach (var sw in m.Switches.Take(3))
                {
                    tried++;
                    var node = graph.Graph!.Nodes.First(n => n.Kind == GraphNodeKind.Switch && n.Source == sw.Name && n.State != GraphNodeState.ShaderDefault);
                    graph.SelectedNodeId = node.Id;
                    string p0 = Perm(graph.Graph, "Pixel permutation"), v0 = Perm(graph.Graph, "Vertex permutation");
                    int inputs0 = graph.Graph.ShaderNode!.Inputs.Count;
                    bool was = sw.IsOn;
                    sw.IsOn = !was;
                    Assert.Equal(node.Id, graph.SelectedNodeId);                          // the selection survives the rebuild
                    string p1 = Perm(graph.Graph, "Pixel permutation"), v1 = Perm(graph.Graph, "Vertex permutation");
                    if (p1 != p0 || v1 != v0)
                    {
                        changed++;
                        if (changed <= 3) _output.WriteLine($"{m.Name} / {sw.Name}: pixel {p0} -> {p1}; vertex {v0} -> {v1}; shader inputs {inputs0} -> {graph.Graph.ShaderNode.Inputs.Count}");
                        Assert.NotEmpty(graph.ShaderStats);                               // stats and code follow the new permutation
                    }
                    graph.UndoCommand.Execute(null);
                    Assert.Equal(was, sw.IsOn);
                    Assert.Equal(p0, Perm(graph.Graph, "Pixel permutation"));          // and back
                    Assert.Equal(v0, Perm(graph.Graph, "Vertex permutation"));
                    if (changed >= 3) break;
                }
                if (changed >= 3) break;
            }
            if (changed >= 3) break;
        }
        _output.WriteLine($"{tried} switch toggle(s) tried, {changed} changed the resolved permutation");
        Assert.True(tried > 0);
        Assert.True(changed > 0, "no switch of any Map11 material changed its permutation: the rebuild path is untested");
    }

    // ================================================================================================ a champion skin in the Character window

    [Fact]
    public async Task ALilliaSkinMaterialIsEditedInTheGraph_AndSavedThroughTheCharacterWindowsOwnSave()
    {
        if (!HaveChampion("Lillia")) return;
        const string bin = "data/characters/lillia/skins/skin49.bin";
        var (vm, project) = OpenScratchProject("lillia");
        Assert.True((bool)Call(vm, "MakeCharacterWadReadable", Path.Combine(Final, "Champions", "Lillia.wad.client"))!);
        vm.MeshPreview.SetChromaSkin(bin);
        using var shaders = new ShaderRig();

        object?[] args = { HashAlgorithms.WadPath(bin), null };
        Assert.True((bool)typeof(MainWindowViewModel).GetMethod("TryResolveEntry", Private)!.Invoke(vm, args)!);
        var entry = (WadAssetEntry)args[1]!;
        byte[] served0 = ReadAsset(vm, entry.PathHash);
        var editor = vm.MeshPreview.MaterialEditor;
        editor.LoadThumbnail = _ => null;
        editor.LoadTextureRaw = null;
        editor.Load(MaterialDocument.Parse(served0, Name, Path_, p => { try { return (byte[])Call(vm, "ReadAssetByPath", p)!; } catch { return null; } }), entry, served0);
        byte[] baseline = editor.Serialize()!;

        var legs = editor.Materials.Single(m => m.Name.EndsWith("Lillia_Skin49_Leg_inst", StringComparison.OrdinalIgnoreCase));
        var graph = new MaterialGraphViewModel(null);
        graph.ShowMaterial(legs.Model, shaders.Cache, shaders.Perms, editor);
        Assert.True(graph.CanEdit);

        var tile = graph.Graph!.Nodes.First(n => n.Source == "Blend_Tile");
        graph.SelectedNodeId = tile.Id;
        var row = Assert.IsType<GraphParamEditRow>(Assert.Single(graph.EditRows));
        row.Components[0].Text = "6";
        row.Components[1].Text = "6";
        row.Components[1].CommitCommand.Execute(null);
        string now = legs.Model.Parameters.Single(p => p.Name == "Blend_Tile").CurrentText;
        _output.WriteLine("Blend_Tile -> " + now);
        Assert.StartsWith("6, 6", now);
        Assert.True(editor.IsDirty);

        var log = new List<string>();
        ((ReyEngine.Core.Diagnostics.Logger)typeof(MainWindowViewModel).GetField("_log", Private)!.GetValue(vm)!).Logged += e => { lock (log) log.Add($"{e.Level} {e.Category}: {e.Message}"); };
        await graph.SaveCommand.ExecuteAsync(null);               // the Character window's own save: copies the skin's bin into the project and writes it
        foreach (var line in log) _output.WriteLine(line);
        Assert.Equal(editor.IsDirty, graph.IsEditorDirty);
        // the save went to the PROJECT (a placed file or an override record - never Riot's WAD): find what it wrote
        var written = Directory.EnumerateFiles(project.RootPath!, "*.bin", SearchOption.AllDirectories).ToList();
        _output.WriteLine(string.Join(" | ", written.Select(f => Path.GetRelativePath(project.RootPath!, f))));
        var servedNow = Assert.Single(written, f => new FileInfo(f).Length == editor.Serialize()!.Length);
        var back = MaterialDocument.Parse(File.ReadAllBytes(servedNow), Name, Path_);
        Assert.StartsWith("6, 6", back.Materials.Single(m => m.Name.EndsWith("Lillia_Skin49_Leg_inst", StringComparison.OrdinalIgnoreCase))
            .Parameters.Single(p => p.Name == "Blend_Tile").CurrentText);

        graph.UndoCommand.Execute(null);
        Assert.Equal(baseline, editor.Serialize());
        graph.Dispose();
    }

    // ================================================================================================ read-only, with the reason

    [Fact]
    public void ALinkedBinMaterialIsReadOnlyInTheGraph_AndSaysSo()
    {
        if (!HaveMap) return;   // Nexus's mesh and materials are map content (M777): Map11.wad
        using var shaders = new ShaderRig();
        using var archive = WadArchive.Open(Map11Wad, new WadPathResolver(Database.Value!));
        const string bin = "data/characters/nexus/skins/skin31.bin";
        if (!archive.TryGetEntry(HashAlgorithms.WadPath(bin), out var skin)) return;
        byte[] bytes = archive.Extract(skin.PathHash);
        // the linked bins come from the champion's own WAD or the shared Global one: read what resolves
        byte[]? Read(string path)
        {
            if (archive.TryGetEntry(HashAlgorithms.WadPath(path), out var e)) return archive.Extract(e.PathHash);
            return null;
        }
        var doc = MaterialDocument.Parse(bytes, Name, Path_, Read);
        var linked = doc.Materials.FirstOrDefault(m => m.IsLinked);
        Assert.NotNull(linked);

        var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService(), LoadThumbnail = _ => null };
        editor.Load(doc, new WadAssetEntry { Path = bin, IsResolved = true, PathHash = skin.PathHash }, bytes);
        var vmLinked = editor.Materials.Single(m => ReferenceEquals(m.Model, linked));
        using var graph = new MaterialGraphViewModel(null);
        graph.ShowMaterial(vmLinked.Model, shaders.Cache, shaders.Perms, editor);

        Assert.False(graph.CanEdit);
        Assert.Contains("linked bin", graph.EditNote);
        Assert.Contains(vmLinked.LinkedFromBin!, graph.EditNote);
        Assert.Empty(graph.EditRows);
        Assert.False(graph.SaveCommand.CanExecute(null));
        _output.WriteLine(graph.EditNote);
    }
}
