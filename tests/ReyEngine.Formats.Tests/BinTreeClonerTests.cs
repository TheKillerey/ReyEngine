using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M756: a duplicated EMPTY option keeps its element type. Riot writes emitterLinger as option&lt;f32&gt;
/// whether or not it holds a value; LeagueToolkit's public constructor infers the type from the value, so
/// the clone used to be written with element type None - on every emitter the editor duplicated (M651).
/// Found by M757's round trip through league-mod's own reader, which compares the wire form.
/// </summary>
public sealed class BinTreeClonerTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static BinTreeOptional EmptyF32()
    {
        // the reader is the only public way to an empty typed option: write a set one, read it, empty it
        var tree = new BinTree(new[] { new BinTreeObject(H("o"), H("C"), new BinTreeProperty[]
            { new BinTreeOptional(H("emitterLinger"), new BinTreeF32(0, 1f)) }) }, Array.Empty<string>());
        using var ms = new MemoryStream();
        tree.Write(ms);
        var read = new BinTree(new MemoryStream(ms.ToArray()));
        var option = (BinTreeOptional)read.Objects[H("o")].Properties[H("emitterLinger")];
        option.Value = null;
        return option;
    }

    [Fact]
    public void AnEmptyOptionKeepsItsElementType()
    {
        var empty = EmptyF32();
        Assert.Equal(BinPropertyType.F32, empty.ValueType);

        var clone = (BinTreeOptional)BinTreeCloner.Clone(empty, H("emitterLinger"));
        Assert.Null(clone.Value);
        Assert.Equal(BinPropertyType.F32, clone.ValueType);
    }

    [Fact]
    public void TheCloneWritesTheSameBytesAsTheOriginal()
    {
        byte[] Write(BinTreeProperty p)
        {
            var tree = new BinTree(new[] { new BinTreeObject(H("o"), H("C"), new[] { p }) }, Array.Empty<string>());
            using var ms = new MemoryStream();
            tree.Write(ms);
            return ms.ToArray();
        }
        var empty = EmptyF32();
        Assert.Equal(Write(empty), Write(BinTreeCloner.Clone(empty, empty.NameHash)));
    }

    [Fact]
    public void ASetOptionStillClonesItsValue()
    {
        var set = new BinTreeOptional(H("period"), new BinTreeF32(0, 2.5f));
        var clone = (BinTreeOptional)BinTreeCloner.Clone(set, set.NameHash);
        Assert.Equal(2.5f, ((BinTreeF32)clone.Value!).Value);
        Assert.Equal(BinPropertyType.F32, clone.ValueType);
    }
}
