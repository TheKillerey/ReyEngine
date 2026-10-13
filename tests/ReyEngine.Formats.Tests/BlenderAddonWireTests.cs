using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using ReyEngine.Formats.Interop;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M581/M841: the add-on's half of the wire against this side's half.
///
/// <para>The two halves live in different languages and, since M841, in different repositories: the add-on
/// is MapgeoAddon, and its wire code is <c>reyengine_link_protocol.py</c> (plain Python, no bpy). A field
/// written in the wrong order does not fail - it decodes into plausible nonsense - so the add-on's OWN code is
/// run here and its output read by the real C# codec, and the other way round.</para>
///
/// <para>Where MapgeoAddon is: the folder in <c>REY_MAPGEOADDON_DIR</c>. Unset (or no Python on the machine),
/// these tests have nothing to check and pass without asserting, like the other machine-dependent tests.</para>
/// </summary>
public sealed class BlenderAddonWireTests
{
    private static string? AddonDir()
    {
        string? dir = System.Environment.GetEnvironmentVariable("REY_MAPGEOADDON_DIR");
        return dir is { Length: > 0 } && File.Exists(Path.Combine(dir, "reyengine_link_protocol.py")) ? dir : null;
    }

    /// <summary>Run Python with the add-on's protocol module imported as <c>p</c>; returns what it printed.</summary>
    private static string? RunPython(string body)
    {
        if (AddonDir() is not { } addon) return null;
        string script = Path.Combine(Path.GetTempPath(), "rey_wire_" + Guid.NewGuid().ToString("N") + ".py");
        File.WriteAllText(script, $$"""
import importlib.util, json, struct, sys
spec = importlib.util.spec_from_file_location("p", r"{{Path.Combine(addon, "reyengine_link_protocol.py")}}")
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)
""" + System.Environment.NewLine + body);
        try
        {
            var psi = new ProcessStartInfo("python", $"-I \"{script}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(psi);
            if (process is null) return null;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
            if (process.ExitCode != 0) throw new InvalidOperationException("python failed: " + error);
            return output.Trim();
        }
        catch (System.ComponentModel.Win32Exception) { return null; }   // no python on this machine
        finally { try { File.Delete(script); } catch { } }
    }

    [Fact]
    public void WhatTheAddOnWritesIsWhatThisSideReads()
    {
        string? hex = RunPython("""
entries = [{
    "name": "sru_wall", "index": 7,
    "pivot": (1.0, 2.0, 3.0), "location": (10.0, 20.0, 30.0),
    "rotation": (0.0, 90.0, 0.0), "scale": (2.0, 2.0, 2.0),
    "positions": [0.0, 0.0, 0.0, 100.0, 0.0, 0.0, 0.0, 100.0, 0.0],
    "normals": [0.0, 0.0, -1.0] * 3,
    "uvs": [0.0, 0.0, 1.0, 0.0, 0.0, 1.0],
    "indices": [0, 1, 2],
    "submeshes": [{"material": "Wall_MAT", "start": 0, "count": 3}],
}]
print(p.encode_meshes(entries).hex())
""");
        if (hex is null) return;

        var mesh = Assert.Single(BlenderBridgeProtocol.DecodeMeshes(Convert.FromHexString(hex)));
        Assert.Equal(7, mesh.Index);
        Assert.Equal("sru_wall", mesh.Name);
        Assert.Equal(new Vector3(1, 2, 3), mesh.Pivot);
        Assert.Equal(new Vector3(10, 20, 30), mesh.Location);
        Assert.Equal(new Vector3(0, 90, 0), mesh.RotationDegrees);
        Assert.Equal(new Vector3(2, 2, 2), mesh.Scale);
        Assert.Equal(new float[] { 0, 0, 0, 100, 0, 0, 0, 100, 0 }, mesh.Positions);
        Assert.Equal(new float[] { 0, 0, -1, 0, 0, -1, 0, 0, -1 }, mesh.Normals);
        Assert.Equal(new float[] { 0, 0, 1, 0, 0, 1 }, mesh.Uvs);
        Assert.Null(mesh.LightmapUvs);
        Assert.Equal(new uint[] { 0, 1, 2 }, mesh.Indices);
        Assert.Equal(new BridgeSubmesh("Wall_MAT", 0, 3), Assert.Single(mesh.Submeshes));
    }

    [Fact]
    public void WhatThisSideWritesIsWhatTheAddOnReads()
    {
        var sent = new BridgeMesh(12, "sru_floor", new Vector3(1, 2, 3),
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 }, new float[] { 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0 },
            new float[] { 0, 0, 1, 0, 0, 1, 1, 1 }, new uint[] { 0, 1, 2, 2, 1, 3 },
            new Vector3(4, 5, 6), new Vector3(0, 45, 0), Vector3.One)
        {
            LightmapUvs = new float[] { 0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f, 0.875f, 1f },
            Colors = Enumerable.Range(0, 16).Select(i => i / 16f).ToArray(),
            Submeshes = new[] { new BridgeSubmesh("Grass_MAT", 0, 3), new BridgeSubmesh("Rock_MAT", 3, 3) },
        };
        string hex = Convert.ToHexString(BlenderBridgeProtocol.EncodeMeshes(new[] { sent }));
        string? json = RunPython($$"""
m = p.decode_meshes(bytes.fromhex("{{hex}}"))[0]
print(json.dumps({k: (list(v) if isinstance(v, tuple) else v) for k, v in m.items()}))
""");
        if (json is null) return;

        var m = JsonDocument.Parse(json).RootElement;
        Assert.Equal(12, m.GetProperty("index").GetInt32());
        Assert.Equal("sru_floor", m.GetProperty("name").GetString());
        Assert.Equal(sent.LightmapUvs, m.GetProperty("lightmap_uvs").EnumerateArray().Select(v => v.GetSingle()));
        Assert.Equal(sent.Colors, m.GetProperty("colors").EnumerateArray().Select(v => v.GetSingle()));
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 3 }, m.GetProperty("indices").EnumerateArray().Select(v => v.GetInt32()));
        var submeshes = m.GetProperty("submeshes").EnumerateArray().ToList();
        Assert.Equal("Rock_MAT", submeshes[1].GetProperty("material").GetString());
        Assert.Equal(3, submeshes[1].GetProperty("start").GetInt32());
        Assert.Equal(45f, m.GetProperty("rotation")[1].GetSingle());
    }

    [Fact]
    public void TheAddOnsMaterialDiffIsAnEditRequestThisSideReads()
    {
        // MapgeoAddon's material editor keeps its state as JSON strings on the material; the diff of two of
        // them is the edit_material body. Read here by the parser the server uses.
        string? json = RunPython("""
before = {"parameters": json.dumps([{"name": "TintColor", "value": [1, 1, 1, 1]}]),
          "samplers": json.dumps([{"textureName": "DiffuseTexture", "texturePath": "a.tex", "addressU": 0}]),
          "switches": json.dumps([{"name": "USE_FOG", "on": True}]),
          "shader_macros": json.dumps({"NO_BAKED_LIGHTING": "1"})}
after = {"parameters": json.dumps([{"name": "TintColor", "value": [1, 0.5, 1, 1]}]),
         "samplers": json.dumps([{"textureName": "DiffuseTexture", "texturePath": "b.tex", "addressU": 1}]),
         "switches": json.dumps([{"name": "USE_FOG", "on": False}]),
         "shader_macros": json.dumps({"REY_GRAPH": "8"})}
edits, notes = p.material_edits("Rock_MAT", before, after)
print(json.dumps({"op": "edit_material", "edits": edits}))
""");
        if (json is null) return;

        var edits = BlenderLinkRequests.MaterialEdits(JsonDocument.Parse(json).RootElement, out int unreadable);
        Assert.Equal(0, unreadable);
        Assert.Equal(new[] { "param", "sampler", "address", "switch", "macro", "macro" }, edits.Select(e => e.Kind));
        Assert.Equal(new[] { 1, 0.5, 1, 1 }, edits[0].Values);
        Assert.Equal("b.tex", edits[1].Text);
        Assert.Equal(1, edits[2].Address['u']);
        Assert.False(edits[3].On);
        Assert.Equal("8", edits.Single(e => e.Name == "REY_GRAPH").Text);
        Assert.True(edits.Single(e => e.Name == "NO_BAKED_LIGHTING").Remove);
    }

    [Fact]
    public void TheAddOnsGeometryHelpersAgreeWithTheBasis()
    {
        // League (x, y, z) -> Blender (x, z, y), its own inverse; per-loop gathers follow the index order;
        // the change fingerprint is text (a crc32 overflows a Blender int property).
        string? output = RunPython("""
x, y, z = p.to_league_axes([1.0, 2.0, 3.0, -4.0, 5.0, -6.0])
loops = p.per_loop([0.0, 0.0, 1.0, 0.0, 0.0, 1.0, 1.0, 1.0], [0, 1, 2, 2, 1, 3], 2)
class Arr(list):
    def foreach_get(self, a, out): out[:] = self._flat
class M:
    def __init__(self, co, loops):
        self.vertices = Arr([None] * (len(co) // 3)); self.vertices._flat = co
        self.loops = Arr([None] * len(loops)); self.loops._flat = loops
fp = p.fingerprint(M([0.0, 0.0, 0.0, 1.0, 0.0, 0.0], [0, 1]))
print(json.dumps({"axes": list(x) + list(y) + list(z), "loops": loops, "fp": type(fp).__name__,
                  "flip": p.flip_v([0.25, 0.25]), "winding": p.reverse_winding([0, 1, 2]),
                  "protocol": p.PROTOCOL, "port": p.DEFAULT_PORT, "limit": p.MAX_VERTICES_PER_MESH}))
""");
        if (output is null) return;

        var r = JsonDocument.Parse(output).RootElement;
        Assert.Equal(new float[] { 1, -4, 3, -6, 2, 5 }, r.GetProperty("axes").EnumerateArray().Select(v => v.GetSingle()));
        Assert.Equal(new float[] { 0, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 1 }, r.GetProperty("loops").EnumerateArray().Select(v => v.GetSingle()));
        Assert.Equal("str", r.GetProperty("fp").GetString());
        Assert.Equal(new[] { 0.25f, 0.75f }, r.GetProperty("flip").EnumerateArray().Select(v => v.GetSingle()));
        Assert.Equal(new[] { 0, 2, 1 }, r.GetProperty("winding").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal(BlenderBridgeProtocol.Version, r.GetProperty("protocol").GetInt32());
        Assert.Equal(BlenderBridgeProtocol.DefaultPort, r.GetProperty("port").GetInt32());
        Assert.Equal(65536, r.GetProperty("limit").GetInt32());
    }
}
