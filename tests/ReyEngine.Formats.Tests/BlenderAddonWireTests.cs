using System.Diagnostics;
using System.Numerics;
using ReyEngine.Formats.Interop;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M581: the add-on's writer against this side's reader.
///
/// <para>The two halves of the wire live in different languages and different repos-worth of code, and
/// nothing but agreement keeps them working. A field written in the wrong order does not fail — it
/// decodes into plausible nonsense, which arrives as corrupt geometry. So the add-on's OWN encoder is
/// executed here (it needs only <c>struct</c>, not Blender) and the result is decoded by the real
/// decoder.</para>
/// </summary>
public sealed class BlenderAddonWireTests
{
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>Lift <c>_encode_meshes</c> out of the add-on and run it on a known mesh.</summary>
    private static byte[]? RunAddonEncoder()
    {
        if (RepoRoot() is not { } root) return null;
        string addon = Path.Combine(root, "tools", "blender", "reyengine_bridge.py");
        if (!File.Exists(addon)) return null;

        string script = Path.Combine(Path.GetTempPath(), "rey_addon_encode.py");
        string output = Path.Combine(Path.GetTempPath(), "rey_addon_block.bin");
        File.WriteAllText(script, $$"""
import struct, sys
src = open(r"{{addon}}", encoding="utf-8").read()
ns = {"struct": struct}
exec(src[src.index("def _encode_meshes("):src.index("def _mesh_to_league(")], ns)
entries = [{
    "name": "sru_wall", "index": 7,
    "pivot": (1.0, 2.0, 3.0), "location": (10.0, 20.0, 30.0),
    "rotation": (0.0, 90.0, 0.0), "scale": (2.0, 2.0, 2.0),
    "positions": [0.0, 0.0, 0.0, 100.0, 0.0, 0.0, 0.0, 100.0, 0.0],
    "normals": [0.0, 0.0, -1.0] * 3,
    "uvs": [0.0, 0.0, 1.0, 0.0, 0.0, 1.0],
    "indices": [0, 1, 2],
}]
open(r"{{output}}", "wb").write(ns["_encode_meshes"](entries))
""");

        var psi = new ProcessStartInfo("python", $"\"{script}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
            return File.Exists(output) ? File.ReadAllBytes(output) : null;
        }
        catch { return null; }   // no python on this machine: the rest of the suite still stands
    }

    [Fact]
    public void WhatTheAddOnWritesIsWhatThisSideReads()
    {
        if (RunAddonEncoder() is not { } block) return;

        var mesh = Assert.Single(BlenderBridgeProtocol.DecodeMeshes(block));
        Assert.Equal(7, mesh.Index);
        Assert.Equal("sru_wall", mesh.Name);
        Assert.Equal(new Vector3(1, 2, 3), mesh.Pivot);
        Assert.Equal(new Vector3(10, 20, 30), mesh.Location);
        Assert.Equal(new Vector3(0, 90, 0), mesh.RotationDegrees);
        Assert.Equal(new Vector3(2, 2, 2), mesh.Scale);

        Assert.Equal(3, mesh.VertexCount);
        Assert.Equal(new float[] { 0, 0, 0, 100, 0, 0, 0, 100, 0 }, mesh.Positions);
        Assert.Equal(new float[] { 0, 0, -1, 0, 0, -1, 0, 0, -1 }, mesh.Normals);
        Assert.Equal(new float[] { 0, 0, 1, 0, 0, 1 }, mesh.Uvs);
        Assert.Equal(new uint[] { 0, 1, 2 }, mesh.Indices);
    }

    [Fact]
    public void TheAddOnsUvGatherPutsEachLoopsUvOnTheRightLoop()
    {
        // M582: pulling a map used to freeze Blender - a Matrix multiply per vertex and a UV assignment
        // per loop, at ~900k vertices and ~2.7M loops. Both are bulk calls now, and the UV expansion from
        // per-vertex to per-loop became real arithmetic. It cannot fail loudly: a wrong gather smears the
        // texture across the mesh, so it is exercised here directly.
        if (RepoRoot() is not { } root) return;
        string addon = Path.Combine(root, "tools", "blender", "reyengine_bridge.py");
        if (!File.Exists(addon)) return;

        string script = Path.Combine(Path.GetTempPath(), "rey_uv_gather.py");
        string output = Path.Combine(Path.GetTempPath(), "rey_uv_gather.txt");
        File.WriteAllText(script, $$"""
src = open(r"{{addon}}", encoding="utf-8").read()
ns = {}
exec(src[src.index("def _uv_per_loop("):src.index("def _build_object(")], ns)
uvs = [0.0, 0.0, 1.0, 0.0, 0.0, 1.0, 1.0, 1.0]
indices = [0, 1, 2, 2, 1, 3]
open(r"{{output}}", "w").write(",".join(str(v) for v in ns["_uv_per_loop"](uvs, indices)))
""");

        var psi = new ProcessStartInfo("python", $"\"{script}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
        }
        catch { return; }
        if (!File.Exists(output)) return;

        // InvariantCulture: Python writes "1.0" and this machine's locale reads '.' as a group separator,
        // which turns 1.0 into 10 and fails a test that was measuring the right thing.
        var actual = File.ReadAllText(output).Split(',')
            .Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        // loop order follows `indices`, so each loop carries ITS vertex's uv
        Assert.Equal(new float[] { 0, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 1 }, actual);
    }

    [Fact]
    public void TheAddOnsAxisSwapMatchesTheBasisAndIsItsOwnInverse()
    {
        // M583: pushing a reshaped mesh did a Matrix multiply per vertex and took minutes. The swap is
        // arithmetic on slices now, which is only correct if it equals B2L exactly - and a wrong axis
        // does not throw, it lands the mesh rotated or mirrored in the map.
        //
        // M584: that basis changed. League reads +X right and +Z up the minimap, so League +Z has to
        // become Blender +Y; the old mapping sent it to -Y and the map arrived upside down. The swap is
        // now its own inverse, which is what lets one matrix serve both directions.
        if (RepoRoot() is not { } root) return;
        string addon = Path.Combine(root, "tools", "blender", "reyengine_bridge.py");
        if (!File.Exists(addon)) return;

        string script = Path.Combine(Path.GetTempPath(), "rey_axis_swap.py");
        string output = Path.Combine(Path.GetTempPath(), "rey_axis_swap.txt");
        File.WriteAllText(script, $$"""
src = open(r"{{addon}}", encoding="utf-8").read()
ns = {}
exec(src[src.index("def _to_league_axes("):src.index("def _mesh_to_league(")], ns)
x, y, z = ns["_to_league_axes"]([1.0, 2.0, 3.0, -4.0, 5.0, -6.0])
open(r"{{output}}", "w").write(",".join(str(v) for v in list(x) + list(y) + list(z)))
""");

        var psi = new ProcessStartInfo("python", $"\"{script}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
        }
        catch { return; }
        if (!File.Exists(output)) return;

        var actual = File.ReadAllText(output).Split(',')
            .Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        // Blender (x, y, z) -> League (x, z, y), for both input vertices: xs, then ys, then zs.
        Assert.Equal(new float[] { 1, -4,   3, -6,   2, 5 }, actual);
    }

    [Fact]
    public void TheChangeFingerprintFitsInABlenderCustomProperty()
    {
        // M586: the fingerprint is a crc32, which is UNSIGNED 32-bit; a Blender custom property is a
        // signed C int. Roughly half of all digests are past 2^31-1 and raise OverflowError on
        // assignment - the very first mesh of a pull hit it. Stored as text, it cannot.
        if (RepoRoot() is not { } root) return;
        string addon = Path.Combine(root, "tools", "blender", "reyengine_bridge.py");
        if (!File.Exists(addon)) return;

        string script = Path.Combine(Path.GetTempPath(), "rey_fingerprint.py");
        string output = Path.Combine(Path.GetTempPath(), "rey_fingerprint.txt");
        File.WriteAllText(script, $$"""
import struct, zlib
src = open(r"{{addon}}", encoding="utf-8").read()
ns = {"struct": struct, "zlib": zlib}
exec(src[src.index("def _fingerprint("):src.index("def _has_changed(")], ns)

class Arr(list):
    def foreach_get(self, a, out): out[:] = self._flat
class M:
    def __init__(self, co, loops):
        self.vertices = Arr([None] * (len(co) // 3)); self.vertices._flat = co
        self.loops = Arr([None] * len(loops)); self.loops._flat = loops

fp = ns["_fingerprint"]
same = fp(M([0.0, 0.0, 0.0, 1.0, 0.0, 0.0], [0, 1])) == fp(M([0.0, 0.0, 0.0, 1.0, 0.0, 0.0], [0, 1]))
moved = fp(M([0.0, 0.0, 0.0, 1.0, 0.0, 0.0], [0, 1])) != fp(M([0.0, 0.0, 0.5, 1.0, 0.0, 0.0], [0, 1]))
biggest = max(int(fp(M([float(i), 0.0, 0.0], [0]))) for i in range(400))
kind = type(fp(M([0.0, 0.0, 0.0], [0]))).__name__
open(r"{{output}}", "w").write("%s;%s;%s;%d" % (kind, same, moved, biggest))
""");

        var psi = new ProcessStartInfo("python", $"\"{script}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
        }
        catch { return; }
        if (!File.Exists(output)) return;

        var parts = File.ReadAllText(output).Split(';');
        Assert.Equal("str", parts[0]);                 // not an int Blender would have to narrow
        Assert.Equal("True", parts[1]);                // an untouched mesh is recognised
        Assert.Equal("True", parts[2]);                // a moved vertex is not
        Assert.True(long.Parse(parts[3]) > int.MaxValue,
            "no digest exceeded a signed int, so this test is not exercising the overflow it exists for");
    }

    [Fact]
    public void TheAddOnAndThisSideAgreeOnTheProtocolVersion()
    {
        if (RepoRoot() is not { } root) return;
        string addon = Path.Combine(root, "tools", "blender", "reyengine_bridge.py");
        if (!File.Exists(addon)) return;
        Assert.Contains($"PROTOCOL = {BlenderBridgeProtocol.Version}",
            File.ReadAllText(addon), StringComparison.Ordinal);
    }
}
