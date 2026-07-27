using System.Buffers.Binary;
using System.Text;
using ReyEngine.Formats.Shaders;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M210. The shader-cache reader and DXBC reflection that the DX11 preview is built on.
///
/// <para>These pin the two decode claims that everything else rests on: the <c>TOC3.0</c> layout, and the
/// permutation key being <c>XXH64(seed 0)</c> over the ordinal-sorted <c>NAME=VALUE</c> concatenation. Both
/// were verified against shipped files (833/833 TOCs consume byte-exactly); the tests here keep them from
/// drifting without needing a game install.</para>
/// </summary>
public class ShaderCacheTests
{
    /// <summary>Build a TOC3.0 buffer to the documented layout, so the parser is checked against the spec
    /// rather than against itself.</summary>
    private static byte[] BuildToc((string, string)[] pool, (ulong Key, uint Blob)[] perms, uint blobCount = 7, uint flag = 1)
    {
        var ms = new MemoryStream();
        void Str(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            var len = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)b.Length);
            ms.Write(len); ms.Write(b);
        }
        void U32(uint v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            ms.Write(b);
        }
        void U64(ulong v)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, v);
            ms.Write(b);
        }

        Str("TOC3.0");
        U32((uint)perms.Length);
        U32((uint)pool.Length);
        U32(blobCount);
        U32(flag);
        Str("baseDefines");
        foreach (var (k, v) in pool) { Str(k); Str(v); }
        Str("shaders");
        foreach (var p in perms) U64(p.Key);
        foreach (var p in perms) U32(p.Blob);
        return ms.ToArray();
    }

    [Fact]
    public void TocParsesEveryFieldAndBothArrays()
    {
        var pool = new[] { ("CLOUD_SHADOWS", "1"), ("DISABLE_FOW", "1"), ("CLOUD_SHADOWS", "0") };
        var perms = new[] { (0xAAAAul, 3u), (0xBBBBul, 108u), (0xCCCCul, 0u) };
        var toc = ShaderCacheReader.ParseToc(BuildToc(pool, perms), "shaders/test.ps.dx11");

        Assert.NotNull(toc);
        Assert.Equal(DxbcStage.Pixel, toc!.Stage);
        Assert.Equal("shaders/test", toc.ShaderName);
        Assert.Equal(3, toc.DefinePool.Count);
        Assert.Equal(3, toc.Permutations.Count);
        Assert.Equal(7u, toc.DeclaredBlobCount);
        Assert.Equal(1u, toc.Flag);

        // the blob-index array is the half the M166 permutation index never needed, and the half the
        // preview cannot load bytecode without
        Assert.Equal(108u, toc.Permutations[1].BlobIndex);
        Assert.Equal(0xBBBBul, toc.Permutations[1].Key);

        // pool collapses to axes: CLOUD_SHADOWS has two distinct values, DISABLE_FOW one
        var axes = toc.Axes;
        Assert.Equal(2, axes.Count);
        Assert.Equal(2, axes.Single(a => a.Name == "CLOUD_SHADOWS").Values.Count);
    }

    [Fact]
    public void AMalformedTocIsRejectedRatherThanGuessed()
    {
        var bad = Encoding.UTF8.GetBytes("not a shader toc at all, really");
        Assert.Null(ShaderCacheReader.ParseToc(bad, "x.ps.dx11"));
    }

    /// <summary>The anchor value for the whole permutation scheme. If this drifts, every lookup silently
    /// misses and the preview loads the wrong bytecode - or none.</summary>
    [Fact]
    public void TheEmptyDefineSetHashesToTheKnownConstant() =>
        Assert.Equal(0xef46db3751d8e999ul, ShaderCacheReader.PermutationKey(Array.Empty<string>()));

    [Fact]
    public void ThePermutationKeyIsOrderIndependent()
    {
        var a = ShaderCacheReader.PermutationKey(new[] { "NO_BAKED_LIGHTING=1", "DISABLE_FOW=1", "BLOOM=0" });
        var b = ShaderCacheReader.PermutationKey(new[] { "BLOOM=0", "NO_BAKED_LIGHTING=1", "DISABLE_FOW=1" });
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentDefineSetsDoNotCollide()
    {
        var a = ShaderCacheReader.PermutationKey(new[] { "DISABLE_FOW=1" });
        var b = ShaderCacheReader.PermutationKey(new[] { "DISABLE_FOW=0" });
        var c = ShaderCacheReader.PermutationKey(Array.Empty<string>());
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(b, c);
    }

    [Fact]
    public void PermutationDescriptionRecoversTheDefineSetFromTheKeyAlone()
    {
        // The TOC stores only key hashes. Recovery works by enumerating the pool and hashing, so a key
        // built the same way must come back with its names attached.
        var pool = new[] { ("A", "1"), ("B", "1") };
        ulong keyAB = ShaderCacheReader.PermutationKey(new[] { "A=1", "B=1" });
        ulong keyA = ShaderCacheReader.PermutationKey(new[] { "A=1" });
        var toc = ShaderCacheReader.ParseToc(BuildToc(pool, new[] { (keyAB, 0u), (keyA, 1u) }), "s.vs.dx11")!;

        var described = ShaderCacheReader.DescribePermutations(toc, out bool truncated);
        Assert.False(truncated);
        Assert.Equal(new[] { "A=1", "B=1" }, described[0].Defines);
        Assert.Equal(new[] { "A=1" }, described[1].Defines);
    }

    [Fact]
    public void ATooLargePoolReportsTruncationRatherThanASilentPartialAnswer()
    {
        // 30 two-value axes is 3^30 combinations - far past any sane cap. The caller must be told, or it
        // would present "define set not recovered" as if those permutations had no defines.
        var pool = Enumerable.Range(0, 30).Select(i => ($"AXIS{i}", "1")).ToArray();
        var toc = ShaderCacheReader.ParseToc(BuildToc(pool, new[] { (1ul, 0u) }), "s.vs.dx11")!;

        var described = ShaderCacheReader.DescribePermutations(toc, out bool truncated, 10_000);
        Assert.True(truncated, "a capped enumeration must report that it was capped");
        Assert.Null(described[0].Defines);
        Assert.Contains("not recovered", described[0].DefineSummary);
    }

    // ---------------------------------------------------------------- M213 permutation resolution

    private static readonly Dictionary<string, string> NoMacros = new();
    private static readonly Dictionary<string, bool> NoSwitches = new();

    /// <summary>The point of the whole exercise: a material's define set has to land on the blob the engine
    /// would pick, not on an arbitrary permutation.</summary>
    [Fact]
    public void AMaterialSwitchPinsTheAxisAndSelectsThatBlob()
    {
        var pool = new[] { ("FEATURE_MASKED", "1"), ("DISABLE_FOW", "1") };
        ulong masked = ShaderCacheReader.PermutationKey(new[] { "FEATURE_MASKED=1" });
        ulong fow = ShaderCacheReader.PermutationKey(new[] { "DISABLE_FOW=1" });
        var toc = ShaderCacheReader.ParseToc(BuildToc(pool, new[] { (masked, 11u), (fow, 22u) }), "s.ps.dx11")!;

        var hit = ShaderCacheReader.ResolvePermutation(
            toc, NoMacros, new Dictionary<string, bool> { ["FEATURE_MASKED"] = true }, null, null, out var why);

        Assert.NotNull(hit);
        Assert.Equal(11u, hit!.BlobIndex);
        Assert.Contains("FEATURE_MASKED=1", why);
    }

    /// <summary>A switch the material turns OFF must not silently match the ON permutation.</summary>
    [Fact]
    public void AnUncookedSwitchValueResolvesToNothingAndSaysWhy()
    {
        var pool = new[] { ("FEATURE_MASKED", "1") };
        ulong masked = ShaderCacheReader.PermutationKey(new[] { "FEATURE_MASKED=1" });
        var toc = ShaderCacheReader.ParseToc(BuildToc(pool, new[] { (masked, 5u) }), "s.ps.dx11")!;

        var hit = ShaderCacheReader.ResolvePermutation(
            toc, NoMacros, new Dictionary<string, bool> { ["FEATURE_MASKED"] = false }, null, null, out var why);

        Assert.Null(hit);
        Assert.Contains("FEATURE_MASKED", why);
    }

    /// <summary>Axes nothing pins are enumerated both ways, because the engine injects some macros per mesh
    /// and they appear in neither the material nor the shader definition. Treating them as definitively
    /// absent is what produced false "this material is broken" verdicts in M166.</summary>
    [Fact]
    public void AnUnpinnedAxisIsTriedBothPresentAndAbsent()
    {
        var pool = new[] { ("NO_BAKED_LIGHTING", "1") };
        ulong withIt = ShaderCacheReader.PermutationKey(new[] { "NO_BAKED_LIGHTING=1" });
        var toc = ShaderCacheReader.ParseToc(BuildToc(pool, new[] { (withIt, 77u) }), "s.ps.dx11")!;

        // the material says nothing about it, yet only the present form is cooked
        var hit = ShaderCacheReader.ResolvePermutation(toc, NoMacros, NoSwitches, null, null, out _);
        Assert.NotNull(hit);
        Assert.Equal(77u, hit!.BlobIndex);
    }

    [Fact]
    public void TheShadersOwnDefaultsAreUsedWhenTheMaterialIsSilent()
    {
        var pool = new[] { ("USE_DYNAMIC_LIGHTING", "1"), ("LOW_QUALITY_MODE", "1") };
        ulong dyn = ShaderCacheReader.PermutationKey(new[] { "USE_DYNAMIC_LIGHTING=1" });
        ulong low = ShaderCacheReader.PermutationKey(new[] { "LOW_QUALITY_MODE=1" });
        var toc = ShaderCacheReader.ParseToc(BuildToc(pool, new[] { (dyn, 3u), (low, 9u) }), "s.ps.dx11")!;

        var hit = ShaderCacheReader.ResolvePermutation(toc, NoMacros, NoSwitches, null,
            new Dictionary<string, bool> { ["USE_DYNAMIC_LIGHTING"] = true, ["LOW_QUALITY_MODE"] = false },
            out var why);

        Assert.NotNull(hit);
        Assert.Equal(3u, hit!.BlobIndex);
        Assert.Contains("shader default", why);
    }

    [Fact]
    public void MacrosBeatSwitchesAndShaderDefaults()
    {
        var pool = new[] { ("DISABLE_FOW", "1"), ("DISABLE_FOW", "0") };
        ulong on = ShaderCacheReader.PermutationKey(new[] { "DISABLE_FOW=1" });
        ulong off = ShaderCacheReader.PermutationKey(new[] { "DISABLE_FOW=0" });
        var toc = ShaderCacheReader.ParseToc(BuildToc(pool, new[] { (on, 1u), (off, 2u) }), "s.ps.dx11")!;

        var hit = ShaderCacheReader.ResolvePermutation(
            toc,
            new Dictionary<string, string> { ["DISABLE_FOW"] = "0" },      // macro says off
            new Dictionary<string, bool> { ["DISABLE_FOW"] = true },       // switch says on
            null, null, out var why);

        Assert.NotNull(hit);
        Assert.Equal(2u, hit!.BlobIndex);
        Assert.Contains("material macro", why);
    }

    [Fact]
    public void TheBasePermutationResolvesWhenNothingIsSet()
    {
        var toc = ShaderCacheReader.ParseToc(
            BuildToc(Array.Empty<(string, string)>(),
                new[] { (ShaderCacheReader.PermutationKey(Array.Empty<string>()), 0u) }), "s.vs.dx11")!;

        var hit = ShaderCacheReader.ResolvePermutation(toc, NoMacros, NoSwitches, null, null, out var why);
        Assert.NotNull(hit);
        Assert.Contains("base permutation", why);
    }

    /// <summary>Failing to resolve is a real answer - it is the condition that makes the live client render
    /// nothing - so it must be reported, never papered over with an arbitrary blob.</summary>
    [Fact]
    public void AnUnmatchableDefineSetReturnsNullRatherThanGuessingABlob()
    {
        var pool = new[] { ("A", "1"), ("B", "1") };
        // only A=1 is cooked; the material demands both
        ulong onlyA = ShaderCacheReader.PermutationKey(new[] { "A=1" });
        var toc = ShaderCacheReader.ParseToc(BuildToc(pool, new[] { (onlyA, 4u) }), "s.ps.dx11")!;

        var hit = ShaderCacheReader.ResolvePermutation(
            toc, new Dictionary<string, string> { ["A"] = "1", ["B"] = "1" }, NoSwitches, null, null, out var why);

        Assert.Null(hit);
        Assert.Contains("Unable to find correct hash", why);
    }

    /// <summary>M225: forcing a define ABSENT must actually remove it. Without pinning, the free-axis
    /// enumeration would add the very define being removed back in, find the original permutation and
    /// report success - a vacuous result, and the same trap M166 documented.</summary>
    [Fact]
    public void ForcingADefineAbsentSelectsThePermutationWithoutIt()
    {
        var pool = new[] { ("DISABLE_FOW", "1") };
        ulong withFow = ShaderCacheReader.PermutationKey(new[] { "DISABLE_FOW=1" });
        ulong without = ShaderCacheReader.PermutationKey(Array.Empty<string>());
        var toc = ShaderCacheReader.ParseToc(
            BuildToc(pool, new[] { (withFow, 7u), (without, 9u) }), "s.ps.dx11")!;

        // the material asks for it...
        var normal = ShaderCacheReader.ResolvePermutation(
            toc, new Dictionary<string, string> { ["DISABLE_FOW"] = "1" }, NoSwitches, null, null, out _);
        Assert.Equal(7u, normal!.BlobIndex);

        // ...and the debug override takes it away
        var forced = ShaderCacheReader.ResolvePermutation(
            toc, new Dictionary<string, string> { ["DISABLE_FOW"] = "1" }, NoSwitches, null, null, out var why,
            forcedAbsent: new HashSet<string> { "DISABLE_FOW" });

        Assert.NotNull(forced);
        Assert.Equal(9u, forced!.BlobIndex);
        Assert.Contains("forced absent", why);
    }

    [Theory]
    [InlineData("shaders/env/foo.vs.dx11", "shaders/env/foo")]
    [InlineData("shaders/env/foo.ps.dx11", "shaders/env/foo")]
    [InlineData("shaders/env/foo", "shaders/env/foo")]
    public void StageSuffixesAreStripped(string path, string expected) =>
        Assert.Equal(expected, ShaderCacheReader.StripStage(path));

    [Fact]
    public void TocPathsAreRebuiltForBothStages()
    {
        Assert.Equal("a/b.vs.dx11", ShaderCacheReader.TocPathFor("a/b", DxbcStage.Vertex));
        Assert.Equal("a/b.ps.dx11", ShaderCacheReader.TocPathFor("a/b", DxbcStage.Pixel));
    }

    [Fact]
    public void NonDxbcBytesAreNotMistakenForAShader()
    {
        Assert.False(DxbcReflection.LooksLikeDxbc(new byte[8]));
        Assert.False(DxbcReflection.LooksLikeDxbc(Encoding.ASCII.GetBytes("DXBC")));   // too short to be real
        Assert.Empty(DxbcReflection.Chunks(Encoding.ASCII.GetBytes("nope")));
    }

    /// <summary>A chunk directory whose offsets point past the buffer must stop, not throw. Shader blobs
    /// come out of a container that over-reports its length, so truncated input is a live possibility.</summary>
    [Fact]
    public void ATruncatedChunkDirectoryDoesNotThrow()
    {
        var b = new byte[64];
        Encoding.ASCII.GetBytes("DXBC").CopyTo(b, 0);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), 4);          // claims 4 chunks
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(32), 9999);       // first one is off the end
        var chunks = DxbcReflection.Chunks(b);
        Assert.Empty(chunks);

        var sh = DxbcReflection.Parse(b);
        Assert.Equal(DxbcStage.Unknown, sh.Stage);
        Assert.Empty(sh.ConstantBuffers);
    }

    [Fact]
    public void ConstantBufferAllocationRoundsUpToARegister()
    {
        // D3D requires the buffer to cover whole 16-byte registers; a 12-byte float3 cbuffer must not be
        // allocated at 12 or CreateBuffer rejects it.
        var cb = new DxbcConstantBuffer("X", 12, 0, Array.Empty<DxbcConstant>());
        Assert.Equal(16, cb.AllocationSize);
        Assert.Equal(560, new DxbcConstantBuffer("Y", 560, 2, Array.Empty<DxbcConstant>()).AllocationSize);
        Assert.Equal(496, new DxbcConstantBuffer("Z", 496, 1, Array.Empty<DxbcConstant>()).AllocationSize);
    }

    [Fact]
    public void SignatureElementsReportWidthAndWhetherTheyAreRead()
    {
        // mask xyz, read-write mask 0 => declared but wholly unread. The input layout still needs it.
        var unread = new DxbcSignatureElement("TEXCOORD", 2, 3, 0b0111, 0, 3, 0);
        Assert.Equal(3, unread.ComponentCount);
        Assert.False(unread.IsRead);
        Assert.Equal("xyz", unread.MaskString);
        Assert.Equal("TEXCOORD2", unread.FullSemantic);

        var read = new DxbcSignatureElement("POSITION", 0, 0, 0b0111, 0b0111, 3, 0);
        Assert.True(read.IsRead);
    }
}
