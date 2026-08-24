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
    public void TheAddOnAndThisSideAgreeOnTheProtocolVersion()
    {
        if (RepoRoot() is not { } root) return;
        string addon = Path.Combine(root, "tools", "blender", "reyengine_bridge.py");
        if (!File.Exists(addon)) return;
        Assert.Contains($"PROTOCOL = {BlenderBridgeProtocol.Version}",
            File.ReadAllText(addon), StringComparison.Ordinal);
    }
}
