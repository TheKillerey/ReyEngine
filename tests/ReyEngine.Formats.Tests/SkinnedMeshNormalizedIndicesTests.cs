using System.Numerics;
using ReyEngine.Formats.Meshes;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M888: a v4 .skn with NORMALIZED_INDICES (flags bit 1) stores each range's indices relative to the
/// range's StartVertex. Read as absolute, every range after the first indexed the first range's vertices -
/// the exploded "box" on Akali skin102, Kai'Sa skin83, Evelynn skin65 and Ahri skin97 (all flags=2).
/// </summary>
public sealed class SkinnedMeshNormalizedIndicesTests
{
    /// <summary>Two ranges of one triangle each. Range A uses vertices 0..2, range B vertices 3..5, and
    /// B's indices are written <paramref name="local"/> (0,1,2) or absolute (3,4,5).</summary>
    private static byte[] TwoRanges(uint flags, bool local)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(SknTangentBaker.Magic);
        w.Write((ushort)4); w.Write((ushort)1);
        w.Write(2u);
        foreach (var (name, sv, si) in new[] { ("A", 0, 0), ("B", 3, 3) })
        {
            var n = new byte[64];
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(n, 0);
            w.Write(n);
            w.Write(sv); w.Write(3); w.Write(si); w.Write(3);
        }
        w.Write(flags);
        w.Write(6u); w.Write(6u);
        w.Write(52u); w.Write(0u);
        for (int i = 0; i < 10; i++) w.Write(0f);
        foreach (ushort i in new ushort[] { 0, 1, 2 }) w.Write(i);
        foreach (ushort i in local ? new ushort[] { 0, 1, 2 } : new ushort[] { 3, 4, 5 }) w.Write(i);
        for (int v = 0; v < 6; v++)
        {
            w.Write((float)v); w.Write(0f); w.Write(0f);
            w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
            w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f);
            w.Write(0f); w.Write(0f); w.Write(1f);
            w.Write(0f); w.Write(0f);
        }
        w.Write(new byte[12]);
        return ms.ToArray();
    }

    [Fact]
    public void NormalizedIndicesAreRebasedOntoTheirRangesStartVertex()
    {
        var bytes = TwoRanges(flags: 2, local: true);
        Assert.True(SkinnedMeshDecoder.HasNormalizedIndices(bytes));
        var mesh = SkinnedMeshDecoder.Decode(bytes);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, mesh.Indices);
    }

    [Fact]
    public void AbsoluteIndicesWithoutTheFlagAreLeftAsStored()
    {
        var bytes = TwoRanges(flags: 0, local: false);
        Assert.False(SkinnedMeshDecoder.HasNormalizedIndices(bytes));
        var mesh = SkinnedMeshDecoder.Decode(bytes);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, mesh.Indices);
    }

    [Fact]
    public void DirectBlendIndicesAloneIsNotTheNormalizedFlag()
    {
        Assert.False(SkinnedMeshDecoder.HasNormalizedIndices(TwoRanges(flags: 1, local: false)));
        Assert.False(SkinnedMeshDecoder.HasNormalizedIndices(new byte[8]));
    }
}
