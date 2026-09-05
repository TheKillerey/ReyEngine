using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Particles;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M648: the rows the particle editor could not explain.
///
/// <para>Re-measuring the VFX census against the current build turned the headline of
/// <c>docs/research/vfx-support-report.md</c> ("26.9% of emitter rows are read-only") into something much
/// smaller and much more specific. Two thirds of the read-only rows are headers that are SUPPOSED to be
/// read-only - a probability table or a struct is a label whose children carry the values. What was left
/// was one bug-shaped thing: <c>AddRows</c> expands a struct only when it has fields, so a struct with
/// none fell through to "Read-only property (unsupported type or Riot reference)". 82,862 of those rows
/// were <c>primitive</c>, whose CLASS is its entire content and decides whether the emitter is a
/// billboard, a mesh, a ray or a trail.</para>
///
/// <para>Measured over 6 shipping map WADs and 25 champion WADs (4,003 VFX bins, 9,846,655 rows), the
/// generic reason fell from 128,001 rows to 3,909, and 7,856 asset-link rows became editable.</para>
/// </summary>
public sealed class ParticlePrimitiveRowTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>The names this test needs resolved; the real host passes its hash dictionary.</summary>
    private static string? Name(uint hash)
    {
        foreach (string n in new[]
        {
            "primitive", "emitterName", "particleName", "complexEmitterDefinitionData", "mMesh",
            "oldAsset", "transform",
            "VfxPrimitiveMesh", "VfxPrimitiveRay", "VfxPrimitiveNonRenderable", "VfxPrimitiveAttachedMesh",
            "VfxMeshDefinitionData",
        })
            if (H(n) == hash) return n;
        return null;
    }

    /// <summary>One emitter carrying the given fields, through the real parse the editor uses - so what
    /// these tests assert is what a user sees, not what a row builder returns in isolation.</summary>
    private static IReadOnlyList<ParticleProperty> Rows(params BinTreeProperty[] emitterFields)
    {
        var emitter = new BinTreeStruct(0, H("VfxEmitterDefinitionData"),
            new BinTreeProperty[] { new BinTreeString(H("emitterName"), "e") }.Concat(emitterFields).ToArray());
        var system = new BinTreeObject(H("sys"), H("VfxSystemDefinitionData"), new BinTreeProperty[]
        {
            new BinTreeString(H("particleName"), "sys"),
            new BinTreeContainer(H("complexEmitterDefinitionData"), BinPropertyType.Struct,
                new BinTreeProperty[] { emitter }),
        });
        using var ms = new MemoryStream();
        new BinTree(new[] { system }, Array.Empty<string>()).Write(ms);
        var doc = ParticleDocument.Parse(ms.ToArray(), Name);
        Assert.NotNull(doc);
        return doc!.Systems.Single().Emitters.Single().Properties;
    }

    /// <summary>The row for one named field.</summary>
    private static ParticleProperty Row(BinTreeProperty prop, string field = "primitive") =>
        Rows(prop).Single(r => r.Name == field);

    private static BinTreeProperty Primitive(string cls, params BinTreeProperty[] fields) =>
        new BinTreeStruct(H("primitive"), H(cls), fields);

    // ===================================================== the empty struct

    [Fact]
    public void AnEmptyStructShowsItsClassInsteadOfAComplaintAboutTypes()
    {
        var row = Row(Primitive("VfxPrimitiveMesh"));

        Assert.Equal("VfxPrimitiveMesh", row.CurrentText);
        Assert.True(row.IsReadOnly);                                   // there is genuinely nothing to type
        Assert.DoesNotContain("unsupported", row.ReadOnlyReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("its class", row.ReadOnlyReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(row.IsDirty);                                     // a constant display row is never dirty
    }

    [Fact]
    public void AnUnnameableClassStillShowsItsHashRatherThanTheWordStruct()
    {
        var row = Row(new BinTreeStruct(H("primitive"), 0xdeadbeef, Array.Empty<BinTreeProperty>()));
        Assert.Equal("0xdeadbeef", row.CurrentText);
    }

    [Fact]
    public void AStructHeaderNamesItsClassToo()
    {
        // Riot's structs are polymorphic; "(1 field(s))" never said which class was in front of you.
        var mesh = Primitive("VfxPrimitiveMesh",
            new BinTreeEmbedded(H("mMesh"), H("VfxMeshDefinitionData"),
                new BinTreeProperty[] { new BinTreeString(H("mSimpleMeshName"), "x.scb") }));
        Assert.Contains(Rows(mesh), r => r.CurrentText.StartsWith("VfxPrimitiveMesh (1 field", StringComparison.Ordinal));
    }

    // ===================================================== the badge

    [Fact]
    public void APrimitiveThePreviewCannotDrawSaysSoOnTheRow()
    {
        // A ray is not a billboard, and the preview has no case for it. Before this the row said nothing.
        var ray = Row(Primitive("VfxPrimitiveRay"));
        Assert.True(ray.IgnoredByPreview);
        Assert.Contains("billboard", ray.PreviewNote!, StringComparison.OrdinalIgnoreCase);

        // A mesh IS drawn as itself, so it carries no warning.
        var mesh = Row(Primitive("VfxPrimitiveMesh"));
        Assert.False(mesh.IgnoredByPreview);
    }

    [Fact]
    public void TheNonRenderablePrimitiveIsCalledOutAsInventedRatherThanDegraded()
    {
        // Falling back to a billboard here does not lose detail - it draws something League never draws,
        // which is the opposite kind of wrong and deserves its own sentence.
        var note = VfxPrimitiveSupport.DegradeNote(H("VfxPrimitiveNonRenderable"));
        Assert.NotNull(note);
        Assert.Contains("does not", note!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("does not", VfxPrimitiveSupport.DegradeNote(H("VfxPrimitiveRay"))!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AttachedMeshIsDescribedAsTheConditionalItIs()
    {
        // It gains geometry only when it names a mesh FILE; the common case is a submesh mask of the host
        // model and stays a billboard. A flat "not supported" would be wrong in both directions.
        var note = VfxPrimitiveSupport.DegradeNote(H("VfxPrimitiveAttachedMesh"));
        Assert.NotNull(note);
        Assert.Contains("mesh file", note!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("submesh mask", note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyThePrimitiveFieldGetsTheBadge()
    {
        // The note is about what the preview draws for a primitive - it must not leak onto every struct.
        var other = Row(new BinTreeStruct(H("mMesh"), H("VfxPrimitiveRay"), Array.Empty<BinTreeProperty>()), "mMesh");
        Assert.False(other.IgnoredByPreview);
    }

    [Fact]
    public void EveryClassTheTableCallsDrawnIsOneTheResolverActuallyBranchesOn()
    {
        // The table is a claim about the renderer, so it is checked against the renderer's source rather
        // than maintained by memory. A class the resolver merely mentions is not one the preview draws -
        // but a class the table calls drawn and the resolver never names is a lie on a row.
        string resolver = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ReyEngine.Formats", "Vfx", "VfxSystemResolver.cs"));
        foreach (string cls in new[]
                 {
                     "VfxPrimitiveMesh", "VfxPrimitiveBeam", "VfxPrimitiveCameraTrail",
                     "VfxPrimitiveArbitraryTrail", "VfxPrimitiveArbitraryQuad",
                 })
        {
            Assert.Null(VfxPrimitiveSupport.DegradeNote(H(cls)));
            Assert.NotNull(VfxPrimitiveSupport.DrawnAs(H(cls)));
            Assert.Contains(cls, resolver, StringComparison.Ordinal);
        }
        // and the ones it does not implement stay badged
        Assert.NotNull(VfxPrimitiveSupport.DegradeNote(H("VfxPrimitiveRay")));
        Assert.NotNull(VfxPrimitiveSupport.DegradeNote(H("VfxPrimitivePlanarProjection")));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    // ===================================================== the types that were read-only for no reason

    [Fact]
    public void AnAssetLinkIsEditableAndWritesBack()
    {
        // 16.17 turned asset references into WadChunkLink (M590). BinValueEditor has read and written them
        // since; only the particle row builder's editable list had not been told, so 7,856 measured rows
        // were read-only for no reason of their own.
        var row = Row(new BinTreeWadChunkLink(H("oldAsset"), HashAlgorithms.WadPath("assets/particles/x.dds")), "oldAsset");
        Assert.False(row.IsReadOnly);
        Assert.Equal(BinTexturePath.Hex(HashAlgorithms.WadPath("assets/particles/x.dds")), row.CurrentText);

        // A path typed in is hashed the way a wad path is hashed, and the row reports the new link.
        row.Apply("assets/particles/y.dds");
        Assert.Equal(BinTexturePath.Hex(HashAlgorithms.WadPath("assets/particles/y.dds")), row.CurrentText);
        Assert.True(row.IsDirty);

        // And the hex it shows goes back in unchanged - the row round-trips rather than re-hashing its
        // own display, which would move the link every time the editor was reopened.
        string shown = row.CurrentText;
        row.Apply(shown);
        Assert.Equal(shown, row.CurrentText);
    }

    [Fact]
    public void AMatrixSaysWhyItIsReadOnlyInsteadOfClaimingItIsUnsupported()
    {
        // It is shown rounded to three decimals for readability. Editing that text back would round every
        // matrix on save, which is a silent corruption, so the row explains itself rather than inviting it.
        var row = Row(new BinTreeMatrix44(H("transform"), System.Numerics.Matrix4x4.Identity), "transform");
        Assert.True(row.IsReadOnly);
        Assert.Contains("precision", row.ReadOnlyReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsupported", row.ReadOnlyReason, StringComparison.OrdinalIgnoreCase);
    }

    // ===================================================== on the real corpus

    [Fact]
    public void TheGenericUnsupportedReasonIsNowRareOnARealChampion()
    {
        string wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions\Aatrox.wad.client";
        if (!File.Exists(wad)) return;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); }
        catch { return; }

        using var archive = WadArchive.Open(wad, new WadPathResolver(db));
        int generic = 0, named = 0, badged = 0, rows = 0;
        foreach (var entry in archive.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
        {
            ParticleDocument? doc;
            try { doc = ParticleDocument.Parse(archive.Extract(entry.PathHash), h => db.TryGetBinName(h, out var n) ? n : null); }
            catch { continue; }
            if (doc is null) continue;
            foreach (var row in doc.Systems.SelectMany(s => s.Properties.Concat(s.Emitters.SelectMany(e => e.Properties))))
            {
                rows++;
                if (row.ReadOnlyReason.StartsWith("Read-only property", StringComparison.Ordinal) && row.IsReadOnly) generic++;
                if (row.CurrentText.StartsWith("VfxPrimitive", StringComparison.Ordinal)) named++;
                if (row.IgnoredByPreview && row.PreviewNote?.Contains("billboard", StringComparison.OrdinalIgnoreCase) == true) badged++;
            }
        }
        if (rows == 0) return;
        Assert.True(named > 0, "no primitive row named its class");
        Assert.True(badged > 0, "no primitive row warned that the preview degrades it");
        // The generic reason survives only for object links and maps - a tiny tail, not a category.
        Assert.True(generic * 100.0 / rows < 0.5, $"the generic read-only reason still covers {generic * 100.0 / rows:0.00}% of {rows} rows");
    }
}
