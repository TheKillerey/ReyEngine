using System.Buffers.Binary;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Materials.Graph;
using ReyEngine.Formats.Shaders;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M829: the Material Graph against Riot's own files - a Map11 SRX_Blend_Master material, a Mantis material and a
/// champion skin material, each checked node by node against the bin it was built from, and the Stats figures
/// checked against the shader's own opcode stream. Read-only: the game, the user's projects and the settings are
/// never written.
///
/// <para>No-ops (not failures) when the game install is absent, the convention of the other real-data tests. The
/// Mantis case needs a Mantis material, which no shipped map uses (they are only in the user's RTX Rift mod
/// build); it runs only where that build output exists.</para>
/// </summary>
public sealed class MaterialGraphRealDataTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string RtxBuild = @"D:\ProjectReyMaps\_ModUpdate\_rtx_rift\build\base_srx.materials.bin";

    private readonly ITestOutputHelper _output;
    public MaterialGraphRealDataTests(ITestOutputHelper output) => _output = output;

    private static bool HaveGame => File.Exists(Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client"))
                                     && File.Exists(Path.Combine(Final, "Champions", "Ahri.wad.client"));

    private sealed class Rig : IDisposable
    {
        public HashSyncService Hashes { get; } = new();
        public ReyEngine.Core.Hashing.HashDatabase Db { get; }
        public WadPathResolver Resolver { get; }
        public ShaderCacheReader Cache { get; }
        public ShaderPermutationIndex? Perms { get; }

        public Rig()
        {
            Db = Hashes.LoadLocal(_ => { });
            Resolver = new WadPathResolver(Db);
            Cache = ShaderCacheReader.Open(Final, Resolver, out _)!;
            try { Perms = new ShaderPermutationIndex(Final, h => Resolver.TryGetPath(h, out var p) ? p : null); } catch { Perms = null; }
        }

        public string? Bin(uint h) => Db.TryGetBinName(h, out var n) ? n : null;
        public string? Wad(ulong h) => Db.TryGetPath(h, out var p) ? p : null;
        public void Dispose() => Cache.Dispose();
    }

    private static MaterialGraph Check(Rig rig, MaterialBinding b, ITestOutputHelper output, out MaterialGraphShaderInfo? info,
        out DxbcShader? vs, out DxbcShader? ps)
    {
        info = MaterialGraphShaderResolver.Resolve(rig.Cache, rig.Perms, b, out string note, out vs, out ps, ReyEngine.App.ViewModels.ShaderPreviewViewModel.ResolveTextureTarget);
        Assert.NotNull(info);
        Assert.True(note.Length == 0, "shader resolution: " + note);
        var g = MaterialGraphBuilder.Build(b, info);
        output.WriteLine($"{b.Name}: {g.Nodes.Count} nodes, {g.Wires.Count} wires, shader {b.RenderShader}");

        // ---- every sampler of the bin is a texture node with the bin's own path ----
        var texNodes = g.Nodes.Where(n => n.Kind == GraphNodeKind.Texture && n.State != GraphNodeState.ShaderDefault).ToList();
        Assert.Equal(b.Slots.Count, texNodes.Count);
        for (int i = 0; i < b.Slots.Count; i++)
        {
            Assert.Equal(b.Slots[i].SamplerName, texNodes.Single(n => n.Id == "tex:" + i).Source);
            Assert.Equal(b.Slots[i].Path, texNodes.Single(n => n.Id == "tex:" + i).TexturePath);
            Assert.Equal(b.Slots[i].ChunkHash, texNodes.Single(n => n.Id == "tex:" + i).TextureChunk);
        }

        // ---- a wired texture feeds the shader input the D3D11 binding would put it on ----
        foreach (var w in g.Wires.Where(w => w.SourceKind == GraphNodeKind.Texture && w.FromNode.StartsWith("tex:")))
        {
            var slot = b.Slots[int.Parse(w.FromNode["tex:".Length..])];
            string? target = info!.TargetFor(slot.SamplerName);
            Assert.NotNull(target);
            string pin = g.Find(w.ToNode)!.Inputs[w.ToPin].Name;
            Assert.Equal(target!.EndsWith("__TX") ? target[..^4] : target, pin);
            // and the permutation really declares it
            Assert.Contains(ps!.Textures.Concat(vs!.Textures), t => t.Name == target);
        }
        // an unwired authored texture is one the permutation compiled out
        foreach (var n in texNodes.Where(n => !g.Wires.Any(w => w.FromNode == n.Id)))
            Assert.True(n.State == GraphNodeState.Unused);

        // ---- parameters, switches and macros are all there, with the bin's values ----
        Assert.Equal(b.Parameters.Count, g.Nodes.Count(n => n.Id.StartsWith("param:")));
        for (int i = 0; i < b.Parameters.Count; i++)
        {
            var node = g.Find("param:" + i)!;
            Assert.Equal(b.Parameters[i].Name, node.Source);
            bool declared = info!.Constants.Any(c => c.Name.Equals(b.Parameters[i].Name, StringComparison.OrdinalIgnoreCase));
            bool wired = g.Wires.Any(w => w.FromNode == node.Id);
            if (node.State == GraphNodeState.Authored) Assert.True(declared && wired);
            if (!declared) Assert.False(wired);
        }
        Assert.Equal(b.SwitchEntries.Count, g.Nodes.Count(n => n.Id.StartsWith("sw:")));
        for (int i = 0; i < b.SwitchEntries.Count; i++)
        {
            var node = g.Find("sw:" + i)!;
            Assert.Equal(b.SwitchEntries[i].Name, node.Source);
            Assert.Equal(b.SwitchEntries[i].On ? "ON" : "OFF", node.Outputs[0].Detail);
        }
        Assert.Equal(b.MacroEntries.Count, g.Nodes.Count(n => n.Id.StartsWith("mac:")));
        for (int i = 0; i < b.MacroEntries.Count; i++)
            Assert.Equal("= " + b.MacroEntries[i].Value, g.Find("mac:" + i)!.Outputs[0].Detail);

        // a shader-default switch is only ever drawn for a name the material does not author
        foreach (var d in g.Nodes.Where(n => n.State == GraphNodeState.ShaderDefault && n.Kind == GraphNodeKind.Switch))
            Assert.DoesNotContain(b.SwitchEntries, s => s.Name.Equals(d.Source, StringComparison.OrdinalIgnoreCase));

        // ---- the output node carries the bin's render state ----
        var o = g.OutputNode!;
        Assert.Equal(b.BlendEnable ? "ON" : "OFF", o.Inputs.Single(p => p.Name == "Blend").Detail);
        Assert.Equal(b.DepthEnable ? "ON" : "OFF", o.Inputs.Single(p => p.Name == "Depth test").Detail);
        Assert.StartsWith(b.WriteMask + ":", o.Inputs.Single(p => p.Name == "Write mask").Detail);
        Assert.Equal(b.PassAuthors("blendEnable"), o.Inputs.Single(p => p.Name == "Blend").Shaded);

        // ---- the layout is deterministic and nothing overlaps ----
        var again = MaterialGraphBuilder.Build(b, info);
        Assert.Equal(g.Nodes.Select(n => (n.Id, n.X, n.Y)), again.Nodes.Select(n => (n.Id, n.X, n.Y)));
        for (int i = 0; i < g.Nodes.Count; i++)
            for (int j = i + 1; j < g.Nodes.Count; j++)
            {
                var a = g.Nodes[i]; var c = g.Nodes[j];
                Assert.True(a.Right <= c.X || c.Right <= a.X || a.Bottom <= c.Y || c.Bottom <= a.Y, $"{a.Id} overlaps {c.Id}");
            }
        return g;
    }

    private static List<MaterialBinding> Map11Materials(Rig rig)
    {
        using var map = WadArchive.Open(Path.Combine(Final, "Maps", "Shipping", "Map11.wad.client"), rig.Resolver);
        var all = new List<MaterialBinding>();
        foreach (var e in map.Entries.Where(e => e.Path != null && e.Path.EndsWith(".materials.bin") && e.Path.Contains("map11/")))
        {
            try { all.AddRange(MaterialDocument.Parse(map.Extract(e.PathHash), rig.Bin, rig.Wad).Materials); } catch { }
        }
        return all;
    }

    [Fact]
    public void AMap11SrxBlendMasterMaterialGraphMatchesItsBin()
    {
        if (!HaveGame) return;
        using var rig = new Rig();
        var all = Map11Materials(rig);
        Assert.NotEmpty(all);

        // the SRX_Blend_Master materials with the most going on, and one with switches
        var picks = all.Where(m => (m.RenderShader ?? "").EndsWith("SRX_Blend_Master", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Slots.Count + m.Parameters.Count + m.SwitchEntries.Count + m.MacroEntries.Count).Take(3).ToList();
        Assert.NotEmpty(picks);
        foreach (var b in picks)
        {
            var g = Check(rig, b, _output, out var info, out var vs, out var ps);
            Assert.Equal("SRX_Blend_Master", g.ShaderNode!.Title);
            Assert.True(g.Wires.Count >= 2);
        }
    }

    [Fact]
    public void MapStandardMaterialsAcrossManyShadersAllBuildAndLayOutCleanly()
    {
        if (!HaveGame) return;
        using var rig = new Rig();
        var all = Map11Materials(rig);

        // one material of each of the 12 most common shaders: the builder must never throw or overlap on real data
        var picks = all.Where(m => !string.IsNullOrEmpty(m.RenderShader)).GroupBy(m => m.RenderShader!.ToLowerInvariant())
            .OrderByDescending(g => g.Count()).Take(12).Select(g => g.OrderByDescending(m => m.Slots.Count + m.Parameters.Count).First()).ToList();
        foreach (var b in picks)
        {
            var info = MaterialGraphShaderResolver.Resolve(rig.Cache, rig.Perms, b, out _, out _, out _);
            var g = MaterialGraphBuilder.Build(b, info);
            Assert.NotEmpty(g.Nodes);
            Assert.All(g.Wires, w => Assert.NotNull(g.Find(w.FromNode)));
            Assert.All(g.Nodes, n => Assert.True(n.Width > 0 && n.Height > 0));
        }
    }

    [Fact]
    public void AChampionSkinMaterialGraphMatchesItsBin_AndItsPixelShaderStatsMatchTheOpcodeStream()
    {
        if (!HaveGame) return;
        using var rig = new Rig();
        using var ahri = WadArchive.Open(Path.Combine(Final, "Champions", "Ahri.wad.client"), rig.Resolver);
        var doc = MaterialDocument.Parse(ahri.Extract(HashAlgorithms.WadPath("data/characters/ahri/skins/skin0.bin")), rig.Bin, rig.Wad);
        var body = doc.Materials.Single(m => m.Name.EndsWith("MAT_Body_inst"));

        var g = Check(rig, body, _output, out var info, out var vs, out var ps);
        Assert.Equal("2_Diffuse_Lerp", g.ShaderNode!.Title);
        Assert.Contains(g.Nodes, n => n.Source == "Diffuse_Texture" && n.State == GraphNodeState.Authored);

        // Stats: the figure is counted from the opcode stream, and the STAT dword it replaced (normal samples) agrees with sample+sample_b
        var stats = DxbcStats.Read(ps!)!;
        Assert.True(stats.Instructions > 0);
        Assert.Equal(CountOps(ps!, 69, 74), stats.TextureSamples);
        Assert.Equal(CountOps(ps!, 69, 74, normalOnly: true), stats.StatNormalSamples);
        _output.WriteLine($"PS: {stats}  VS: {DxbcStats.Read(vs!)}");
        var rows = MaterialGraphStats.Shader(vs, ps);
        Assert.Equal(stats.Instructions.ToString("n0"), rows.Single(r => r.Label == "Instructions").Pixel);
        Assert.Equal($"{ps!.Samplers.Count()} / 16", rows.Single(r => r.Label == "Texture samplers").Pixel);
    }

    [Fact]
    public void AMantisMaterialGraphMatchesItsBin()
    {
        if (!HaveGame || !File.Exists(RtxBuild)) return;
        using var rig = new Rig();
        var doc = MaterialDocument.Parse(File.ReadAllBytes(RtxBuild), rig.Bin, rig.Wad);
        var mantis = doc.Materials.Where(m => (m.RenderShader ?? "").Contains("Mantis", StringComparison.OrdinalIgnoreCase)).ToList();
        if (mantis.Count == 0) return;

        var b = mantis.OrderByDescending(m => m.Slots.Count).First();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var g = Check(rig, b, _output, out var info, out var vs, out var ps);
        _output.WriteLine($"mantis graph built and checked in {started.ElapsedMilliseconds} ms");
        Assert.StartsWith("Mantis", g.ShaderNode!.Title);
        // structural only: the mod's content changes, the shape of the graph does not
        Assert.NotEmpty(g.ShaderNode.Inputs);
        foreach (var sw in g.Nodes.Where(n => n.Kind == GraphNodeKind.Switch && n.State == GraphNodeState.Authored))
            Assert.Contains(sw.Outputs[0].Detail, new[] { "ON", "OFF" });

        // Stats: sample opcodes
        Assert.Equal(CountOps(ps!, 69, 74), DxbcStats.Read(ps!)!.TextureSamples);

        // every Mantis material of the build graphs without throwing (no timing: that depends on the machine)
        foreach (var m in mantis) Assert.NotEmpty(MaterialGraphBuilder.Build(m, info).Nodes);
    }

    /// <summary>A vertex shader that fetches with sample_l (cloth, TFT flowmaps) has STAT dword 14 = 0, so the figure
    /// is counted from the opcodes: it must be above zero there, and equal to the opcode count everywhere.</summary>
    [Fact]
    public void VertexShadersThatSampleReportTheirSamples_NotTheStatDwordThatLeavesSampleLOut()
    {
        if (!HaveGame) return;
        using var rig = new Rig();
        int checkedShaders = 0, withSamples = 0;
        foreach (var name in rig.Cache.ShaderNames())
        {
            string path = ShaderCacheReader.TocPathFor(name, DxbcStage.Vertex);
            var toc = rig.Cache.ReadToc(path);
            if (toc is null || toc.Permutations.Count == 0) continue;
            var vs = rig.Cache.LoadShader(path, toc.Permutations[0].BlobIndex, out _);
            if (vs is null || !vs.Textures.Any()) continue;
            var stats = DxbcStats.Read(vs);
            if (stats is null) continue;
            checkedShaders++;
            Assert.Equal(CountOps(vs, 69, 74), stats.TextureSamples);
            Assert.Equal(CountOps(vs, 69, 74, normalOnly: true), stats.StatNormalSamples);
            if (stats.TextureSamples > 0) withSamples++;
        }
        _output.WriteLine($"{checkedShaders} vertex shaders with textures checked, {withSamples} fetch");
        Assert.True(checkedShaders >= 5);
        Assert.True(withSamples >= 1, "some vertex shader should fetch a texture");
    }

    /// <summary>The champion convention: a skin's default diffuse is a sampler named just "texture", which no shader
    /// declares. The graph must wire it to the SAME shader input the preview binds it to.</summary>
    [Fact]
    public void ASkinsGenericTextureSamplerIsWiredToTheInputThePreviewBinds()
    {
        if (!HaveGame) return;
        using var rig = new Rig();
        using var ahri = WadArchive.Open(Path.Combine(Final, "Champions", "Ahri.wad.client"), rig.Resolver);
        var doc = MaterialDocument.Parse(ahri.Extract(HashAlgorithms.WadPath("data/characters/ahri/skins/skin0.bin")), rig.Bin, rig.Wad);
        var generic = doc.Materials.FirstOrDefault(m => m.Slots.Any(s => s.SamplerName.Equals("texture", StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(generic);

        // the preview falls back to this shader for a material with none (DefaultShaderHint)
        const string shader = "assets/shaders/generated/shaders/skinnedmesh/diffuse_alpha";
        string vsPath = ShaderCacheReader.TocPathFor(shader, DxbcStage.Vertex), psPath = ShaderCacheReader.TocPathFor(shader, DxbcStage.Pixel);
        var vs = rig.Cache.LoadShader(vsPath, rig.Cache.ReadToc(vsPath)!.Permutations[0].BlobIndex, out _)!;
        var ps = rig.Cache.LoadShader(psPath, rig.Cache.ReadToc(psPath)!.Permutations[0].BlobIndex, out _)!;
        string? expected = ReyEngine.App.ViewModels.ShaderPreviewViewModel.ResolveTextureTarget("texture", ps, vs);
        Assert.NotNull(expected);
        Assert.DoesNotContain("_Shared", expected!, StringComparison.OrdinalIgnoreCase);

        var info = MaterialGraphShaderInfo.FromReflection("shaders/skinnedmesh/diffuse_alpha", vs, ps,
            resolveTexture: s => ReyEngine.App.ViewModels.ShaderPreviewViewModel.ResolveTextureTarget(s, ps, vs));
        var g = MaterialGraphBuilder.Build(generic!, info);

        int slot = generic!.Slots.ToList().FindIndex(s => s.SamplerName.Equals("texture", StringComparison.OrdinalIgnoreCase));
        var wire = g.Wires.SingleOrDefault(w => w.FromNode == "tex:" + slot);
        Assert.NotNull(wire);
        Assert.Equal(expected!.EndsWith("__TX") ? expected[..^4] : expected, g.Find(wire!.ToNode)!.Inputs[wire.ToPin].Name);
    }

    /// <summary>Opcodes lo..hi of the SHEX stream (sample family 69..74; normalOnly = sample and sample_b).</summary>
    private static int CountOps(DxbcShader sh, uint lo, uint hi, bool normalOnly = false)
    {
        foreach (var (tag, off, size) in DxbcReflection.Chunks(sh.Bytecode))
        {
            if (tag is not ("SHEX" or "SHDR")) continue;
            int count = 0, i = 2, tokens = size / 4;
            while (i < tokens)
            {
                uint t = BinaryPrimitives.ReadUInt32LittleEndian(sh.Bytecode.AsSpan(off + i * 4, 4));
                uint op = t & 0x7FF;
                int len = (int)((t >> 24) & 0x7F);
                if (op == 53) len = (int)BinaryPrimitives.ReadUInt32LittleEndian(sh.Bytecode.AsSpan(off + (i + 1) * 4, 4));
                if (len <= 0) break;
                if (op >= lo && op <= hi && (!normalOnly || op is 69 or 74)) count++;
                i += len;
            }
            return count;
        }
        return -1;
    }
}
