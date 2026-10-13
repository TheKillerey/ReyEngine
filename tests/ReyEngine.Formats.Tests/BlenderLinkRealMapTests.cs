using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Interop;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M841: the Blender link against a REAL Map11 project - base_srx.mapgeo and its materials.bin copied out of the
/// game into a temp project folder, opened by the real <see cref="MainWindowViewModel"/>, served by the real
/// <see cref="BlenderBridgeServer"/> over a socket: hello, pull, materials, texture, assign_material and
/// edit_material, then ReyEngine's own Save, and the two files read back.
///
/// <para>Needs the game install (skipped without it); a few seconds where it is there.</para>
/// </summary>
public sealed class BlenderLinkRealMapTests : IDisposable
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string MapgeoPath = "data/maps/mapgeometry/map11/base_srx.mapgeo";
    private const string MaterialsPath = "data/maps/mapgeometry/map11/base_srx.materials.bin";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rey-m841-link-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static object? Call(object o, string name, params object?[] args)
    {
        var m = o.GetType().GetMethods(Any).First(x => x.Name == name && x.GetParameters().Length == args.Length);
        try { return m.Invoke(o, args); }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
    }

    private static (JsonElement Header, byte[] Body) Send(NetworkStream stream, object request)
    {
        BlenderBridgeProtocol.WriteMessage(stream, JsonSerializer.Serialize(request));
        var header = JsonDocument.Parse(BlenderBridgeProtocol.ReadHeader(stream)!).RootElement.Clone();
        int bytes = header.TryGetProperty("bytes", out var b) ? b.GetInt32() : 0;
        return (header, bytes > 0 ? BlenderBridgeProtocol.ReadExactly(stream, bytes) : Array.Empty<byte>());
    }

    [Fact]
    public async Task ARealMap11ProjectIsServedAndWhatTheLinkAppliesIsWhatSaveWrites()
    {
        string wadPath = Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client");
        if (!File.Exists(wadPath)) return;   // no game install on this machine

        // ---- a project folder holding a copy of the map
        string folder = Path.Combine(_root, "Map11");
        var db = new HashSyncService().LoadLocal(_ => { });
        using (var wad = WadArchive.Open(wadPath, new WadPathResolver(db)))
            foreach (string p in new[] { MapgeoPath, MaterialsPath })
            {
                string file = Path.Combine(folder, p.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, wad.Extract(HashAlgorithms.WadPath(p)));
            }
        string mapFile = Path.Combine(folder, MapgeoPath.Replace('/', Path.DirectorySeparatorChar));
        string binFile = Path.Combine(folder, MaterialsPath.Replace('/', Path.DirectorySeparatorChar));
        byte[] mapBefore = File.ReadAllBytes(mapFile);

        var vm = new MainWindowViewModel { Project = ReyProjectService.OpenFolder(_root) };
        vm.MaterialEditor.LoadThumbnail = null;   // texture thumbnails are Avalonia bitmaps: no render platform in a test
        Call(vm, "BuildMounts");
        var args = new object?[] { HashAlgorithms.WadPath(MapgeoPath), null };
        Assert.True((bool)Call(vm, "TryResolveEntry", args)!);
        // The map is opened the way LoadMapGeoAsync leaves it - decoded with the editor's own extended-channel
        // set, held with its entry and raw bytes. LoadMapGeoAsync itself hands its result to the UI thread
        // (Dispatcher.UIThread.InvokeAsync), which nothing pumps in a test; the headless end-to-end harness
        // (.codex_tmp/LinkProbe) runs the real open under Avalonia.Headless instead.
        var entry = (WadAssetEntry)args[1]!;
        byte[] raw = File.ReadAllBytes(mapFile);
        var map = MapGeoDecoder.Decode(raw, (IReadOnlySet<string>)Call(vm, "ExtendedChannelMaterialsFor", entry.Path)!);
        foreach (var (field, value) in new (string, object)[] { ("_currentMap", map), ("_currentMapBytes", raw), ("_currentMapEntry", entry) })
            typeof(MainWindowViewModel).GetField(field, Any)!.SetValue(vm, value);

        // ---- the real handlers, on a real socket (inline: the test is the editor's only thread here)
        using var server = new BlenderBridgeServer(a => a(), (_, _) => { })
        {
            Handlers = (BlenderBridgeHandlers)Call(vm, "BlenderHandlers")!,
            HandlerTimeout = TimeSpan.FromMinutes(3),
            Token = BlenderBridgeServer.NewToken(),
        };
        Assert.True(server.Start(0, out string? error), error);
        using var client = new TcpClient();
        client.Connect(System.Net.IPAddress.Loopback, server.Port);
        var stream = client.GetStream();

        var (hello, _) = Send(stream, new { op = "hello", protocol = 3, client = "MapgeoAddon test", token = server.Token!.ToLowerInvariant() });
        Assert.Equal("hello", hello.GetProperty("op").GetString());
        Assert.Equal(MapgeoPath, hello.GetProperty("map_path").GetString());
        Assert.Equal(MaterialsPath, hello.GetProperty("materials_bin").GetString());

        var (pulled, block) = Send(stream, new { op = "pull" });
        var meshes = BlenderBridgeProtocol.DecodeMeshes(block);
        Assert.Equal(map.Meshes.Count, meshes.Count);
        Assert.Equal(meshes.Count, pulled.GetProperty("meshes").GetArrayLength());
        Assert.All(meshes, m => Assert.Equal(m.Indices.Length, m.Submeshes.Sum(s => s.IndexCount)));

        var (materials, _) = Send(stream, new { op = "materials" });
        Assert.True(materials.GetProperty("editable").GetBoolean(), materials.ToString().Length > 600 ? materials.ToString()[..600] : materials.ToString());
        var mats = materials.GetProperty("materials");
        Assert.True(mats.EnumerateObject().Count() > 100);
        var usedNames = meshes.SelectMany(m => m.Submeshes).Select(s => s.Material).Where(n => n.Length > 0).Distinct().ToList();
        Assert.All(usedNames, n => Assert.True(mats.TryGetProperty(n, out var d) && d.GetProperty("rey").GetProperty("used").GetBoolean(), n));

        // a texture a used material samples, from the editor's mounts
        string texture = usedNames.SelectMany(n => mats.GetProperty(n).GetProperty("samplerValues").EnumerateArray())
            .Select(s => s.GetProperty("texturePath").GetString() ?? "").First(p => p.EndsWith(".tex", StringComparison.OrdinalIgnoreCase));
        var (tex, texBytes) = Send(stream, new { op = "texture", path = texture });
        Assert.True(tex.GetProperty("found").GetBoolean(), texture);
        Assert.Equal("game", tex.GetProperty("source").GetString());
        Assert.Equal("TEX", System.Text.Encoding.ASCII.GetString(texBytes, 0, 3));
        var (notTexture, notBytes) = Send(stream, new { op = "texture", path = MaterialsPath });   // only .tex/.dds are served
        Assert.False(notTexture.GetProperty("found").GetBoolean());
        Assert.Empty(notBytes);

        // assign a single-submesh mesh another used material, and edit a vector parameter
        var target = meshes.First(m => m.Submeshes.Count == 1 && m.VertexCount > 3);
        string oldMaterial = target.Submeshes[0].Material;
        string newMaterial = usedNames.First(n => n != oldMaterial);
        var (assigned, _) = Send(stream, new { op = "assign_material", assignments = new object[]
        {
            new { mesh = target.Index, submesh = 0, material = newMaterial },
            new { mesh = target.Index, submesh = 0, material = "not/a/material" },
        } });
        Assert.Equal(1, assigned.GetProperty("applied").GetInt32());
        Assert.Contains("not a material", assigned.GetProperty("refused")[0].GetProperty("reason").GetString());

        var (editName, param) = mats.EnumerateObject()
            .Where(m => m.Name != oldMaterial && m.Name != newMaterial && !m.Value.GetProperty("shaderMacros").TryGetProperty("REY_GRAPH", out _))
            .SelectMany(m => m.Value.GetProperty("paramValues").EnumerateArray()
                .Where(p => p.TryGetProperty("value", out var v) && v.GetArrayLength() == 4 && p.GetProperty("type").GetString() == "Vector4")
                .Select(p => (m.Name, p.GetProperty("name").GetString()!)))
            .First();
        var (edited, _) = Send(stream, new { op = "edit_material", edits = new object[]
        {
            new { material = editName, kind = "param", name = param, value = new[] { 0.375, 0.5, 0.625, 1.0 } },
            new { material = editName, kind = "param", name = "NoSuchParameter", value = new[] { 1.0 } },
        } });
        Assert.Equal(1, edited.GetProperty("applied").GetInt32());
        Assert.Contains("no parameter", edited.GetProperty("refused")[0].GetProperty("reason").GetString());

        // the same value again is "unchanged", not a second edit
        var (again, _) = Send(stream, new { op = "edit_material", edits = new object[]
            { new { material = editName, kind = "param", name = param, value = new[] { 0.375, 0.5, 0.625, 1.0 } } } });
        Assert.Equal(0, again.GetProperty("applied").GetInt32());
        Assert.Equal(0, again.GetProperty("refused").GetArrayLength());
        Assert.Contains("1 unchanged", again.GetProperty("detail").GetString());

        // a type-18 (WadChunkLink) sampler pointed at another texture the game has: it stays a link on the wire
        var (linkMaterial, sampler, oldPath) = mats.EnumerateObject()
            .Where(m => m.Name != oldMaterial && m.Name != newMaterial)
            .SelectMany(m => m.Value.GetProperty("samplerValues").EnumerateArray()
                .Where(x => x.GetProperty("texturePathType").GetInt32() == 18 && (x.GetProperty("texturePath").GetString() ?? "").EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                .Select(x => (m.Name, x.GetProperty("textureName").GetString()!, x.GetProperty("texturePath").GetString()!)))
            .First(x => !string.Equals(x.Item3, texture, StringComparison.OrdinalIgnoreCase));
        var (sampled, _) = Send(stream, new { op = "edit_material", edits = new object[]
        {
            new { material = linkMaterial, kind = "sampler", name = sampler, path = texture },
            new { material = linkMaterial, kind = "sampler", name = sampler, path = "assets/maps/no/such/texture.tex" },
        } });
        Assert.Equal(1, sampled.GetProperty("applied").GetInt32());
        Assert.Contains("neither in the project nor in the game", sampled.GetProperty("refused")[0].GetProperty("reason").GetString());

        // pending in the editor, nothing written yet
        var mesh = map.Meshes.First(m => m.Index == target.Index);
        Assert.True(mesh.HasMaterialEdit);
        Assert.True(vm.MaterialEditor.IsDirty);
        Assert.Equal(mapBefore, File.ReadAllBytes(mapFile));
        Assert.Contains(vm.UndoService.UndoHistory, c => c is MeshMaterialAssignmentCommand);

        // ---- ReyEngine's Save, then the files are read back
        await ((Task)Call(vm, "SaveMeshMoves")!).WaitAsync(TimeSpan.FromMinutes(5));
        await ((Task)Call(vm, "SaveMaterialOverride")!).WaitAsync(TimeSpan.FromMinutes(2));

        var reread = MapGeoDecoder.Decode(File.ReadAllBytes(mapFile));
        Assert.Equal(new[] { newMaterial }, reread.Meshes.First(m => m.Index == target.Index).Materials);
        Assert.Equal(MapGeoDecoder.Decode(mapBefore).Meshes.Count(m => m.Materials.Contains(newMaterial)) + 1,
                     reread.Meshes.Count(m => m.Materials.Contains(newMaterial)));
        string? Bin(uint h) => db.TryGetBinName(h, out var n) ? n : null;
        string? Wad(ulong h) => db.TryGetPath(h, out var p) ? p : null;
        var saved = MaterialDocument.Parse(File.ReadAllBytes(binFile), Bin, Wad)
            .Materials.First(m => m.Name == editName).Parameters.First(p => p.Name == param);
        Assert.True(saved.TryGetVector4(out var v4));
        Assert.Equal(new System.Numerics.Vector4(0.375f, 0.5f, 0.625f, 1f), v4);

        var savedSlot = MaterialDocument.Parse(File.ReadAllBytes(binFile), Bin, Wad)
            .Materials.First(m => m.Name == linkMaterial).Slots.First(x => x.SamplerName == sampler);
        Assert.True(savedSlot.IsWadChunkLink);                                  // type 18 kept
        Assert.Equal(HashAlgorithms.WadPath(texture), savedSlot.ChunkHash);
        Assert.NotEqual(HashAlgorithms.WadPath(oldPath), savedSlot.ChunkHash);

        // ---- the stale-entry case: the tab holds the RIOT copy of the same chunk (opened before Copy To Project)
        var binArgs = new object?[] { HashAlgorithms.WadPath(MaterialsPath), null };
        Assert.True((bool)Call(vm, "TryResolveEntry", binArgs)!);
        var projectBin = (WadAssetEntry)binArgs[1]!;
        Assert.Equal(AssetSourceKind.ProjectFolder, projectBin.SourceKind);
        var riotCopy = new WadAssetEntry { Path = MaterialsPath, PathHash = projectBin.PathHash, IsResolved = true, SourceKind = AssetSourceKind.RiotReference };
        byte[] binBytes = File.ReadAllBytes(binFile);
        vm.MaterialEditor.Load(MaterialDocument.Parse(binBytes, Bin, Wad), riotCopy, binBytes);
        var row = vm.MaterialEditor.Materials.SelectMany(m => m.Parameters).First(r => r.IsEditable && r.Model.TryGetVector4(out _));
        row.EditedText = "0.25, 0.25, 0.25, 0.25";
        row.ApplyCommand.Execute(null);
        Assert.True(vm.MaterialEditor.IsDirty);
        var ensure = new object?[] { projectBin, null };
        Assert.False((bool)Call(vm, "EnsureMapMaterialsInEditor", ensure)!);   // unsaved edits to the stale copy: refused, kept
        Assert.Contains("older copy", (string)ensure[1]!);
        Assert.Same(riotCopy, vm.MaterialEditor.BinEntry);

        vm.MaterialEditor.Load(MaterialDocument.Parse(binBytes, Bin, Wad), riotCopy, binBytes);   // clean again
        Assert.True((bool)Call(vm, "EnsureMapMaterialsInEditor", ensure)!);    // clean: reloaded from the project copy
        Assert.Equal(AssetSourceKind.ProjectFolder, vm.MaterialEditor.BinEntry!.SourceKind);
    }
}
