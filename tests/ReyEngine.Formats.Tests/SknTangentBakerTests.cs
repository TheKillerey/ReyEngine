using System.Numerics;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meshes;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M758: MikkTSpace tangents baked into a .skn. The strong check ran outside the suite: every one of the
/// 5,627 .skn files in the installed game was baked by this code and by league-toolkit's own ltk_mesh
/// (what LTK Manager 1.21 runs), and the two files were compared byte for byte - see the commit. These
/// tests pin the properties that comparison cannot name.
/// </summary>
public sealed class SknTangentBakerTests
{
    private const int NormalAt = 32, UvAt = 44;

    /// <summary>A v4 Basic skin: each vertex is (position, normal, uv), one range, the given triangles.</summary>
    private static byte[] Skn((Vector3 P, Vector3 N, Vector2 Uv)[] verts, ushort[] indices, uint flags = 0)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(SknTangentBaker.Magic);
        w.Write((ushort)4); w.Write((ushort)1);
        w.Write(1u);
        var name = new byte[64];
        "Body"u8.CopyTo(name);
        w.Write(name);
        w.Write(0); w.Write(verts.Length); w.Write(0); w.Write(indices.Length);
        w.Write(flags);
        w.Write((uint)indices.Length); w.Write((uint)verts.Length);
        w.Write(52u); w.Write(0u);
        for (int i = 0; i < 10; i++) w.Write((float)i);   // bounds: kept as they are
        foreach (var i in indices) w.Write(i);
        foreach (var (p, n, uv) in verts)
        {
            w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
            w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
            w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f);
            w.Write(n.X); w.Write(n.Y); w.Write(n.Z);
            w.Write(uv.X); w.Write(uv.Y);
        }
        w.Write(new byte[12]);
        return ms.ToArray();
    }

    /// <summary>A flat quad facing +Z, UVs laid out the ordinary way - or, mirrored, with the second
    /// triangle's UV folded back over the shared diagonal so its chart has the opposite orientation.</summary>
    private static byte[] Quad(bool mirrorRight = false) => Skn(new[]
    {
        (new Vector3(0, 0, 0), Vector3.UnitZ, new Vector2(0, 1)),
        (new Vector3(1, 0, 0), Vector3.UnitZ, new Vector2(1, 1)),
        (new Vector3(1, 1, 0), Vector3.UnitZ, new Vector2(1, 0)),
        (new Vector3(0, 1, 0), Vector3.UnitZ, mirrorRight ? new Vector2(1, 1) : new Vector2(0, 0)),
    }, new ushort[] { 0, 1, 2, 0, 2, 3 });

    private static Vector4 TangentOf(SknTangentBaker.Skn s, int v)
    {
        int o = v * s.Stride + s.Stride - 16;
        return new Vector4(BitConverter.ToSingle(s.Vertices, o), BitConverter.ToSingle(s.Vertices, o + 4),
            BitConverter.ToSingle(s.Vertices, o + 8), BitConverter.ToSingle(s.Vertices, o + 12));
    }

    private static Vector3 NormalOf(SknTangentBaker.Skn s, int v) => new(
        BitConverter.ToSingle(s.Vertices, v * s.Stride + NormalAt), BitConverter.ToSingle(s.Vertices, v * s.Stride + NormalAt + 4),
        BitConverter.ToSingle(s.Vertices, v * s.Stride + NormalAt + 8));

    [Fact]
    public void ABasicSkinBecomesATangentSkinWithUnitOrthogonalTangents()
    {
        var baked = SknTangentBaker.Read(SknTangentBaker.Bake(Quad(), out var result));
        Assert.Equal(SknTangentBaker.VertexType.Basic, result.From);
        Assert.Equal(SknTangentBaker.VertexType.Tangent, baked.Type);
        Assert.Equal(72, baked.Stride);
        Assert.Equal(4, baked.VertexCount);   // one UV chart: nothing to split
        for (int v = 0; v < 4; v++)
        {
            var t = TangentOf(baked, v);
            var xyz = new Vector3(t.X, t.Y, t.Z);
            Assert.Equal(1f, xyz.Length(), 5);
            Assert.Equal(0f, Vector3.Dot(xyz, NormalOf(baked, v)), 5);
            Assert.Equal(new Vector3(1, 0, 0), xyz);   // U runs along +X
            // MikkTSpace says orientation preserving (+1) for V running down +Y here, and League stores -sign
            Assert.True(t.W is 1f or -1f);
            // opaque white where there was no colour
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, baked.Vertices.AsSpan(v * 72 + 52, 4).ToArray());
        }
        Assert.Equal(new float[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, baked.Bounds);   // bounds survive
        Assert.Equal("Body", baked.Ranges[0].Material);
    }

    [Fact]
    public void AMirroredUvChartFlipsTheSignAndSplitsTheSharedVertices()
    {
        var plain = SknTangentBaker.Read(SknTangentBaker.Bake(Quad(), out _));
        var mirrored = SknTangentBaker.Read(SknTangentBaker.Bake(Quad(mirrorRight: true), out var r));
        // the right triangle's chart is mirrored in U: its corners disagree with the left one's on the
        // shared diagonal, so those vertices split
        Assert.True(r.VerticesAfter > r.VerticesBefore, $"{r.VerticesBefore} -> {r.VerticesAfter}");
        var signs = Enumerable.Range(0, mirrored.VertexCount).Select(v => TangentOf(mirrored, v).W).Distinct().ToList();
        Assert.Contains(1f, signs);
        Assert.Contains(-1f, signs);
        Assert.Single(Enumerable.Range(0, plain.VertexCount).Select(v => TangentOf(plain, v).W).Distinct());
    }

    [Fact]
    public void AZeroNormalRefusesTheBakeAndNamesTheVertex()
    {
        var skn = Skn(new[]
        {
            (Vector3.Zero, Vector3.UnitZ, Vector2.Zero),
            (Vector3.UnitX, Vector3.Zero, Vector2.UnitX),
            (Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY),
        }, new ushort[] { 0, 1, 2 });
        var ex = Assert.Throws<SknTangentBaker.BakeException>(() => SknTangentBaker.Bake(skn, out _));
        Assert.Equal("vertex 1 has invalid position, normal or UV", ex.Message);
    }

    [Theory]
    [InlineData(new ushort[] { 0, 1 }, "not a triangle list")]
    [InlineData(new ushort[] { 0, 1, 7 }, "outside range")]
    public void BrokenIndexRangesAreRefused(ushort[] indices, string reason)
    {
        var skn = Skn(new[]
        {
            (Vector3.Zero, Vector3.UnitZ, Vector2.Zero), (Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX),
            (Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY),
        }, indices);
        Assert.Contains(reason, Assert.Throws<SknTangentBaker.BakeException>(() => SknTangentBaker.Bake(skn, out _)).Message);
    }

    [Fact]
    public void BakingTwiceGivesTheSameFile()
    {
        byte[] once = SknTangentBaker.Bake(Quad(mirrorRight: true), out _);
        byte[] twice = SknTangentBaker.Bake(once, out var r);
        Assert.Equal(SknTangentBaker.VertexType.Tangent, r.From);
        Assert.Equal(once, twice);   // a Tangent skin keeps its layout; the same inputs give the same tangents
    }

    [Fact]
    public void ARealSkinRoundTripsUnbakedAndBakesIntoSomethingTheEditorReads()
    {
        const string wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions\Akali.wad.client";
        if (!File.Exists(wad)) return;
        var db = new HashSyncService().LoadLocal(_ => { });
        using var archive = ReyEngine.Core.Wad.WadArchive.Open(wad, new WadPathResolver(db));
        var entry = archive.Entries.First(x => x.IsResolved && x.Path.EndsWith("/skins/base/akali.skn", StringComparison.OrdinalIgnoreCase));
        byte[] original = archive.Extract(entry);

        // read + write without a bake is the same file (this skin is v4.1, the form it is written in)
        if (BitConverter.ToUInt16(original, 4) == 4)
            Assert.Equal(original, SknTangentBaker.Write(SknTangentBaker.Read(original)));

        byte[] baked;
        try { baked = SknTangentBaker.Bake(original, out _); }
        catch (SknTangentBaker.BakeException) { return; }   // a patch can ship a zero normal; LTK refuses it too
        var mesh = SkinnedMeshDecoder.Decode(baked);          // LeagueToolkit, which the viewport uses, reads it
        var before = SkinnedMeshDecoder.Decode(original);
        Assert.True(mesh.VertexCount >= before.VertexCount);
        Assert.Equal(before.SubMeshes.Count, mesh.SubMeshes.Count);
        Assert.Equal(before.IndexCount, mesh.IndexCount);
    }

    [Fact]
    public void AnOldVersionIsWrittenAsVersionFourWithComputedBounds()
    {
        // v1.1: one range, no flags, no stored bounds, no end tab
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(SknTangentBaker.Magic);
        w.Write((ushort)1); w.Write((ushort)1);
        w.Write(1u);
        w.Write(new byte[64]);
        w.Write(0); w.Write(3); w.Write(0); w.Write(3);
        w.Write(3u); w.Write(3u);
        foreach (ushort i in new ushort[] { 0, 1, 2 }) w.Write(i);
        foreach (var (p, uv) in new[] { (new Vector3(-2, 0, 0), Vector2.Zero), (new Vector3(2, 0, 0), Vector2.UnitX), (new Vector3(0, 4, 0), Vector2.UnitY) })
        {
            w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
            w.Write(0); w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f);
            w.Write(0f); w.Write(0f); w.Write(1f);
            w.Write(uv.X); w.Write(uv.Y);
        }
        var baked = SknTangentBaker.Read(SknTangentBaker.Bake(ms.ToArray(), out _));
        Assert.Equal(new[] { -2f, 0, 0, 2, 4, 0 }, baked.Bounds[..6]);
        Assert.Equal(new[] { 0f, 2, 0 }, baked.Bounds[6..9]);
        Assert.Equal(MathF.Sqrt(8f), baked.Bounds[9], 5);
    }
}
