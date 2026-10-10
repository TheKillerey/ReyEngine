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
/// M831: wiring on Riot's own Map11 materials.bin, through the real view models and the editor's real save into a scratch folder
/// project: reconnect a texture to another texture input, disconnect one, author an unconnected input, delete a parameter node, save,
/// reparse and check the bin's samplerValues / paramValues are exactly what the gestures meant and every other material is
/// unchanged; then undo each gesture back to byte-identical. Riot's files, the user's projects and the settings are never written.
///
/// <para>No-ops (not failures) when the game install or the hash dictionary is absent, the convention of the other real-data tests.</para>
/// </summary>
public sealed class MaterialGraphWiringRealDataTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string Game = @"C:\Riot Games\League of Legends\Game";
    private const string Final = Game + @"\DATA\FINAL";
    private const string Map11Wad = Final + @"\Maps\Shipping\Map11.wad.client";

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reyengine-m831-" + Guid.NewGuid().ToString("N"));

    public MaterialGraphWiringRealDataTests(ITestOutputHelper output) { _output = output; Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static readonly Lazy<HashDatabase?> Database = new(() => { try { return new HashSyncService().LoadLocal(_ => { }); } catch { return null; } });
    private static string? Name(uint h) => Database.Value is { } db && db.TryGetBinName(h, out var n) ? n : null;
    private static string? Path_(ulong h) => Database.Value is { } db && db.TryGetPath(h, out var p) ? p : null;
    private static bool HaveMap => File.Exists(Map11Wad) && Database.Value is not null;

    private static object? Call(MainWindowViewModel vm, string name, params object?[] args)
    {
        try { return typeof(MainWindowViewModel).GetMethod(name, Private)!.Invoke(vm, args); }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }

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

    private (MainWindowViewModel Vm, ReyProject Project) OpenScratchProject()
    {
        string root = Path.Combine(_dir, "map11");
        Directory.CreateDirectory(root);
        var project = ReyProjectService.OpenFolder(root);
        project.GameDirectory = Game;
        project.ProjectVersion = 2;
        ReyProjectService.Save(project, project.ProjectFilePath!);
        var vm = new MainWindowViewModel { Project = project };
        vm.Settings.AutoSaveEdits = false;
        Call(vm, "BuildMounts");
        return (vm, project);
    }

    private (MainWindowViewModel Vm, ReyProject Project) OpenScratchProject(string name)
    {
        string root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        var project = ReyProjectService.OpenFolder(root);
        project.GameDirectory = Game;
        project.ProjectVersion = 2;
        ReyProjectService.Save(project, project.ProjectFilePath!);
        var vm = new MainWindowViewModel { Project = project };
        vm.Settings.AutoSaveEdits = false;
        Call(vm, "BuildMounts");
        return (vm, project);
    }

    private static List<(string Name, string Path)> Samplers(MaterialBinding m) => m.Slots.Select(s => (s.SamplerName, s.Path)).ToList();
    private static List<(string Name, string Text)> Params(MaterialBinding m) => m.Parameters.Select(p => (p.Name, p.CurrentText)).ToList();

    /// <summary>A Map11 material whose graph has what the gestures need: two or more wired 2D textures, and a parameter that can be removed.</summary>
    private sealed record Pick(string BinPath, string Material);

    private static (byte[] Bytes, Pick Pick)? FindPick(ShaderRig shaders)
    {
        using var map = WadArchive.Open(Map11Wad, new WadPathResolver(Database.Value!));
        foreach (var e in map.Entries.Where(e => e.Path != null && e.Path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase) && e.Path.Contains("map11/", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] bytes;
            MaterialDocument doc;
            try { bytes = map.Extract(e.PathHash); doc = MaterialDocument.Parse(bytes, Name, Path_); } catch { continue; }
            if (doc.Materials.Count < 3) continue;
            var editor = new MaterialEditorViewModel { UndoService = new UndoRedoService() };
            editor.Load(doc, new WadAssetEntry { Path = e.Path, IsResolved = true, PathHash = e.PathHash }, bytes);
            foreach (var m in editor.Materials.Where(m => !m.IsLinked && !string.IsNullOrEmpty(m.Model.RenderShader)).Take(60))
            {
                using var g = new MaterialGraphViewModel(null);
                g.ShowMaterial(m.Model, shaders.Cache, shaders.Perms, editor);
                if (g.Graph?.ShaderNode is null || !g.CanWire) continue;
                var tex = g.Graph.Nodes.Where(n => n.Kind == GraphNodeKind.Texture && n.State == GraphNodeState.Authored && g.Graph.Wires.Any(w => w.FromNode == n.Id)).ToList();
                var open = g.UnconnectedInputs();
                bool param = g.Graph.Nodes.Any(n => n.Kind is GraphNodeKind.Scalar or GraphNodeKind.Vector or GraphNodeKind.Color && n.State == GraphNodeState.Authored);
                if (tex.Count >= 3 && param) return (bytes, new Pick(e.Path, m.Name));
            }
        }
        return null;
    }

    [Fact]
    public async Task Map11Material_ReconnectDisconnectAuthorAndDelete_SaveReparse_AndUndoEachGestureToByteIdentical()
    {
        if (!HaveMap) return;
        using var shaders = new ShaderRig();
        var found = FindPick(shaders);
        Assert.True(found is not null, "no Map11 material with three wired textures and a parameter was found");
        var (original, pick) = found!.Value;
        _output.WriteLine($"{pick.BinPath}: {pick.Material}");

        // ---- a scratch folder project holding the project copy of the bin (what Copy To Project makes)
        var (vm, project) = OpenScratchProject();
        Assert.True((bool)Call(vm, "MakeCharacterWadReadable", Map11Wad)!);
        ulong hash = HashAlgorithms.WadPath(pick.BinPath);
        object?[] resolve = { hash, null };
        Assert.True((bool)typeof(MainWindowViewModel).GetMethod("TryResolveEntry", Private)!.Invoke(vm, resolve)!);
        var riotEntry = (WadAssetEntry)resolve[1]!;
        riotEntry.Path = pick.BinPath;
        riotEntry.IsResolved = true;
        object?[] place = { riotEntry, original, "" };
        Assert.True((bool)Call(vm, "TryPlaceInProjectFolder", place)!);
        Call(vm, "BuildMounts");
        Assert.True((bool)typeof(MainWindowViewModel).GetMethod("TryResolveEntry", Private)!.Invoke(vm, resolve)!);
        var entry = (WadAssetEntry)resolve[1]!;
        entry.Path = pick.BinPath;
        entry.IsResolved = true;
        string file = Path.Combine(project.RootPath!, ((string)place[2]!).Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(file), file);

        byte[] served = (byte[])Call(vm, "ReadAsset", hash)!;
        var editor = vm.MaterialEditor;
        editor.LoadThumbnail = _ => null;
        editor.LoadTextureRaw = null;
        editor.Load(MaterialDocument.Parse(served, Name, Path_), entry, served);
        var baseline = editor.Serialize()!;
        var material = editor.Materials.Single(m => m.Name == pick.Material);
        using var graph = new MaterialGraphViewModel(null);
        graph.ShowMaterial(material.Model, shaders.Cache, shaders.Perms, editor);
        Assert.True(graph.CanEdit);
        Assert.True(graph.CanWire);
        var before = MaterialDocument.Parse(baseline, Name, Path_);
        var beforeMaterial = before.Materials.Single(m => m.Name == pick.Material);
        _output.WriteLine($"{pick.Material}: samplers [{string.Join(", ", Samplers(beforeMaterial).Select(s => s.Name))}]");
        _output.WriteLine($"params [{string.Join(", ", Params(beforeMaterial).Select(s => s.Name))}]");

        var snapshots = new List<byte[]> { baseline };
        void Snap(string what)
        {
            var now = editor.Serialize()!;
            Assert.NotEqual(snapshots[^1], now);                          // the gesture changed the file
            snapshots.Add(now);
            _output.WriteLine($"{what}  ->  {graph.EditStatus}");
        }
        int ShaderPin(string name) => graph.Graph!.ShaderNode!.Inputs.ToList().FindIndex(p => p.Name == name);
        string Shader() => graph.Graph!.ShaderNode!.Id;

        // ---- 1. reconnect: pick a wired 2D texture up by its input end and put it on another texture input; that input takes ONE link, so
        // the sampler that fed it is replaced and the input the texture left is free again
        var wiredTex = graph.Graph!.Nodes.Where(n => n.Kind == GraphNodeKind.Texture && n.State == GraphNodeState.Authored && graph.Graph.Wires.Any(w => w.FromNode == n.Id)).ToList();
        int PinOf(GraphNode n) => graph.Graph!.Wires.First(w => w.FromNode == n.Id).ToPin;
        GraphNode? moving = null, replaced = null;
        foreach (var a in wiredTex)
            foreach (var b in wiredTex)
                if (moving is null && a != b && wiredTex.Count(x => x != a && x != b) >= 1 && graph.CheckLink(a.Id, Shader(), PinOf(b)).Ok) { moving = a; replaced = b; }
        Assert.NotNull(moving);
        string movingSampler = moving!.Source, replacedSampler = replaced!.Source;
        int freedPin = PinOf(moving), replacedPin = PinOf(replaced);
        string freedName = graph.Graph.ShaderNode!.Inputs[freedPin].Name, targetName = graph.Graph.ShaderNode!.Inputs[replacedPin].Name;
        string movedPath = Samplers(beforeMaterial).First(x => x.Name == movingSampler).Path;
        Assert.True(graph.Connect(moving.Id, Shader(), replacedPin, move: true), graph.EditStatus);
        Snap($"moved {movingSampler} -> {targetName} (replacing {replacedSampler})");
        var afterMove = Samplers(material.Model);
        Assert.DoesNotContain(afterMove, s => s.Name == movingSampler);
        Assert.DoesNotContain(afterMove, s => s.Name == replacedSampler && s.Name != targetName);
        Assert.Contains(afterMove, s => s.Name == targetName && s.Path == movedPath);
        Assert.Equal(Samplers(beforeMaterial).Count - 1, afterMove.Count);

        // ---- 2. disconnect another texture
        var victim = graph.Graph!.Nodes.First(n => n.Kind == GraphNodeKind.Texture && n.State == GraphNodeState.Authored && n.Source != targetName
                                                    && graph.Graph.Wires.Any(w => w.FromNode == n.Id));
        int victimPin = graph.Graph.Wires.First(w => w.FromNode == victim.Id).ToPin;
        string victimName = victim.Source;
        Assert.True(graph.Disconnect(victimPin), graph.EditStatus);
        Snap($"disconnected {victimName}");
        Assert.DoesNotContain(Samplers(material.Model), s => string.Equals(s.Name, victimName, StringComparison.OrdinalIgnoreCase));

        // ---- 3. author an unconnected input through the add menu path (CreateAt): the input the moved texture left is free again
        var open = graph.UnconnectedInputs().First(u => u.PinIndex == freedPin);
        Assert.Equal(freedName, open.Name);
        int entriesBefore = material.Model.Slots.Count + material.Model.Parameters.Count;
        Assert.True(graph.CreateAt(open.PinIndex), graph.EditStatus);
        Snap($"created {open.Name} ({(open.IsTexture ? "texture" : "constant")})");
        Assert.Equal(entriesBefore + 1, material.Model.Slots.Count + material.Model.Parameters.Count);
        Assert.Contains(Samplers(material.Model), x => x.Name.Equals(open.Name, StringComparison.OrdinalIgnoreCase));

        // ---- 4. delete a parameter node
        var paramNode = graph.Graph!.Nodes.First(n => n.Kind is GraphNodeKind.Scalar or GraphNodeKind.Vector or GraphNodeKind.Color && n.State == GraphNodeState.Authored);
        string paramName = paramNode.Source;
        Assert.True(graph.DeleteNode(paramNode.Id), graph.EditStatus);
        Snap($"deleted {paramName}");
        Assert.DoesNotContain(Params(material.Model), p => p.Name == paramName);
        Assert.Equal(Params(beforeMaterial).Count - 1, material.Model.Parameters.Count);

        // one undo step per gesture
        Assert.Equal(4, editor.UndoService!.UndoHistory.Count);

        // ---- Save: the editor's own save writes the project's file
        var expectedSamplers = Samplers(material.Model);
        var expectedParams = Params(material.Model);
        await graph.SaveCommand.ExecuteAsync(null);
        byte[] saved = File.ReadAllBytes(file);
        Assert.NotEqual(original, saved);
        var reloaded = MaterialDocument.Parse(saved, Name, Path_);
        var m2 = reloaded.Materials.Single(m => m.Name == pick.Material);
        Assert.Equal(expectedSamplers, Samplers(m2));
        Assert.Equal(expectedParams, Params(m2));
        _output.WriteLine($"saved samplers [{string.Join(", ", Samplers(m2).Select(s => s.Name))}]");

        // every other material in the bin is what it was
        foreach (var m in before.Materials.Where(m => m.Name != pick.Material))
        {
            var other = reloaded.Materials.Single(x => x.Name == m.Name);
            Assert.Equal(Samplers(m), Samplers(other));
            Assert.Equal(Params(m), Params(other));
            Assert.Equal(m.AllSwitches.Select(s => (s.Name, s.On)), other.AllSwitches.Select(s => (s.Name, s.On)));
            Assert.Equal(m.AllMacros.Select(s => (s.Name, s.Value)), other.AllMacros.Select(s => (s.Name, s.Value)));
        }
        Assert.Equal(before.Materials.Count, reloaded.Materials.Count);

        // ---- undo each gesture: the file is byte-identical to the one before it, all the way back
        for (int k = snapshots.Count - 1; k >= 1; k--)
        {
            graph.UndoCommand.Execute(null);
            Assert.Equal(snapshots[k - 1], editor.Serialize());
        }
        Assert.Equal(baseline, editor.Serialize());
        Assert.Equal(Samplers(beforeMaterial), Samplers(material.Model));
        Assert.Equal(Params(beforeMaterial), Params(material.Model));
        // and the Material tab's rows agree with the model again
        Assert.Equal(Samplers(beforeMaterial).Select(s => s.Name), material.Slots.Select(s => s.SamplerName));
        Assert.Equal(Params(beforeMaterial).Select(s => s.Name), material.Parameters.Select(p => p.Name));

        // redo brings the whole sequence back
        for (int k = 1; k < snapshots.Count; k++)
        {
            graph.RedoCommand.Execute(null);
            Assert.Equal(snapshots[k], editor.Serialize());
        }
    }

    /// <summary>A champion skin material in the Character window's editor: a wired texture is disconnected and put back by Undo, and a
    /// connection that would overwrite the skin's built-in texture field is refused with the reason.</summary>
    [Fact]
    public void ALilliaSkinMaterial_DisconnectAndUndoThroughTheCharacterEditor_ByteIdentical()
    {
        string wad = Path.Combine(Final, "Champions", "Lillia.wad.client");
        if (!File.Exists(wad) || Database.Value is null) return;
        const string bin = "data/characters/lillia/skins/skin49.bin";
        var (vm, _) = OpenScratchProject("lillia");
        Assert.True((bool)Call(vm, "MakeCharacterWadReadable", wad)!);
        vm.MeshPreview.SetChromaSkin(bin);
        using var shaders = new ShaderRig();
        object?[] args = { HashAlgorithms.WadPath(bin), null };
        Assert.True((bool)typeof(MainWindowViewModel).GetMethod("TryResolveEntry", Private)!.Invoke(vm, args)!);
        var entry = (WadAssetEntry)args[1]!;
        byte[] served = (byte[])Call(vm, "ReadAsset", entry.PathHash)!;
        var editor = vm.MeshPreview.MaterialEditor;
        editor.LoadThumbnail = _ => null;
        editor.LoadTextureRaw = null;
        editor.Load(MaterialDocument.Parse(served, Name, Path_, p => { try { return (byte[])Call(vm, "ReadAssetByPath", p)!; } catch { return null; } }), entry, served);
        byte[] baseline = editor.Serialize()!;

        using var graph = new MaterialGraphViewModel(null);
        MaterialBindingViewModel? chosen = null;
        foreach (var m in editor.Materials.Where(m => !m.IsLinked && !string.IsNullOrEmpty(m.Model.RenderShader)))
        {
            graph.ShowMaterial(m.Model, shaders.Cache, shaders.Perms, editor);
            if (graph.CanWire && graph.Graph!.Nodes.Any(n => n.Kind == GraphNodeKind.Texture && n.State == GraphNodeState.Authored
                    && graph.Graph.Wires.Any(w => w.FromNode == n.Id)
                    && m.Model.Slots.Any(s => s.SamplerName == n.Source && s.IsRemovable))) { chosen = m; break; }
        }
        Assert.NotNull(chosen);
        var node = graph.Graph!.Nodes.First(n => n.Kind == GraphNodeKind.Texture && n.State == GraphNodeState.Authored && graph.Graph.Wires.Any(w => w.FromNode == n.Id)
            && chosen!.Model.Slots.Any(s => s.SamplerName == n.Source && s.IsRemovable));
        int pin = graph.Graph.Wires.First(w => w.FromNode == node.Id).ToPin;
        _output.WriteLine($"{chosen!.Name}: disconnecting {node.Source}");
        int before = chosen.Model.Slots.Count;
        Assert.True(graph.Disconnect(pin), graph.EditStatus);
        Assert.Equal(before - 1, chosen.Model.Slots.Count);
        Assert.NotEqual(baseline, editor.Serialize());
        Assert.Single(editor.UndoService!.UndoHistory);
        graph.UndoCommand.Execute(null);
        Assert.Equal(baseline, editor.Serialize());
        Assert.Equal(before, chosen.Model.Slots.Count);
        Assert.Equal(before, chosen.Slots.Count);
    }
}
