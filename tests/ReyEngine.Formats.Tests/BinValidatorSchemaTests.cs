using System.IO;
using System.Linq;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M372: the schema-aware half of the bin validator. These are the checks "does it parse" cannot make -
/// a bin can be perfectly well-formed and still be wrong for the patch it is going to be injected into.
///
/// The most important test here is the LAST one: with no schema supplied the validator must behave exactly
/// as it did before, because the meta database is a separate opt-in download and most users will not have
/// synced it.
/// </summary>
public class BinValidatorSchemaTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private const uint MatClass = 0xff9d3409;   // StaticMaterialDef
    private const uint ObjHash = 0x5150CAFE;

    /// <summary>A bin with one object carrying exactly the properties given.</summary>
    private static byte[] Bin(params BinTreeProperty[] props)
    {
        var tree = new BinTree(
            new[] { new BinTreeObject(ObjHash, MatClass, props) }, System.Array.Empty<string>());
        using var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static BinValidationReport Run(byte[] bin,
        Func<uint, bool>? classKnown = null,
        Func<uint, uint, string?>? declaredType = null,
        Func<uint, uint, bool>? declaredEver = null)
        => BinValidator.Validate("test.bin", bin, System.Array.Empty<byte[]>(),
            _ => true,           // every asset exists, so only schema issues can surface
            resolve: h => h == MatClass ? "StaticMaterialDef"
                : h == H("type") ? "type"
                : h == H("name") ? "name"
                : h == H("goneField") ? "goneField"
                : h == H("bogusField") ? "bogusField" : null,
            classKnown: classKnown, declaredType: declaredType, declaredEver: declaredEver);

    // ---- the check that matters most: silence without a schema ----

    [Fact]
    public void WithNoSchemaNothingIsReported()
    {
        // Same bin that trips every check below. With no schema delegates the validator must not invent
        // findings - the meta database is opt-in and most users will not have it.
        var report = Run(Bin(
            new BinTreeString(H("name"), "Test"),
            new BinTreeF32(H("type"), 1f),
            new BinTreeU32(H("bogusField"), 3)));
        Assert.True(report.IsClean);
    }

    [Fact]
    public void AnUnknownClassIsNotTreatedAsAProblem()
    {
        // A class the schema has never heard of means "no expectations", not "everything is wrong".
        var report = Run(Bin(new BinTreeU32(H("bogusField"), 3)),
            classKnown: _ => false,
            declaredType: (_, _) => null);
        Assert.True(report.IsClean);
    }

    // ---- the three findings ----

    [Fact]
    public void FieldTheClassDoesNotDeclareIsReported()
    {
        var report = Run(Bin(new BinTreeU32(H("bogusField"), 3)),
            classKnown: _ => true,
            declaredType: (_, _) => null,
            declaredEver: (_, _) => false);

        var issue = Assert.Single(report.Issues);
        Assert.Equal("field-not-in-class", issue.Category);
        Assert.Contains("bogusField", issue.Detail);
        Assert.Equal(ObjHash, issue.ObjectPathHash);
        Assert.Equal(MatClass, issue.ObjectClassHash);
    }

    /// <summary>The finding that serves "my mod broke after the patch": the field is real, it is just gone
    /// from this build. Different category and different wording from a field that never existed.</summary>
    [Fact]
    public void FieldRemovedInThisPatchIsReportedSeparately()
    {
        var report = Run(Bin(new BinTreeU32(H("goneField"), 3)),
            classKnown: _ => true,
            declaredType: (_, _) => null,
            declaredEver: (_, p) => p == H("goneField"));

        var issue = Assert.Single(report.Issues);
        Assert.Equal("field-removed-in-patch", issue.Category);
        Assert.Contains("earlier build", issue.Detail);
    }

    [Fact]
    public void WrongWireTypeIsReported()
    {
        // 'type' is declared U32 but stored as F32. The bin parses fine; the game reads it at the wrong
        // width. This is precisely what a parse check cannot catch.
        var report = Run(Bin(new BinTreeF32(H("type"), 1f)),
            classKnown: _ => true,
            declaredType: (_, p) => p == H("type") ? "U32" : null);

        var issue = Assert.Single(report.Issues);
        Assert.Equal("field-type-mismatch", issue.Category);
        Assert.Contains("BinTreeF32", issue.Detail);
        Assert.Contains("U32", issue.Detail);
    }

    [Fact]
    public void CorrectlyTypedFieldIsAccepted()
    {
        var report = Run(Bin(new BinTreeU32(H("type"), 1)),
            classKnown: _ => true,
            declaredType: (_, p) => p == H("type") ? "U32" : null);
        Assert.True(report.IsClean);
    }

    /// <summary>Flag is the bit-packed bool. A BinTreeBool in a Flag slot parses and looks right, and the
    /// game reads it at the wrong width - the same trap the write path guards.</summary>
    [Fact]
    public void BoolInAFlagSlotIsReported()
    {
        var report = Run(Bin(new BinTreeBool(H("type"), true)),
            classKnown: _ => true,
            declaredType: (_, p) => p == H("type") ? "Flag" : null);
        Assert.Equal("field-type-mismatch", Assert.Single(report.Issues).Category);
    }

    // ---- deliberate non-checks ----

    [Theory]
    [InlineData("List")]
    [InlineData("List2")]
    [InlineData("Embed")]
    [InlineData("Pointer")]
    [InlineData("Map")]
    [InlineData("Option")]
    [InlineData("Color")]
    [InlineData("Mtx44")]
    public void ContainerAndStructFamiliesAreNotTypeChecked(string declared)
    {
        // Their CLR types genuinely subtype each other (BinTreeEmbedded : BinTreeStruct,
        // BinTreeUnorderedContainer : BinTreeContainer), so an exact-name check would flag correct bins.
        // Staying quiet is the deliberate choice, and this pins it so nobody "fixes" it into false alarms.
        var report = Run(Bin(new BinTreeU32(H("type"), 1)),
            classKnown: _ => true,
            declaredType: (_, p) => p == H("type") ? declared : null);
        Assert.True(report.IsClean, $"{declared} must not be wire-type checked");
    }

    [Fact]
    public void SchemaIssuesDoNotSuppressTheExistingChecks()
    {
        // A missing asset and a schema problem in the same bin must BOTH be reported - the new checks are
        // additive, not a replacement.
        var bin = Bin(
            new BinTreeString(H("name"), "assets/missing/thing.dds"),
            new BinTreeU32(H("bogusField"), 3));
        var report = BinValidator.Validate("test.bin", bin, System.Array.Empty<byte[]>(),
            _ => false,   // nothing exists
            resolve: h => h == MatClass ? "StaticMaterialDef" : null,
            classKnown: _ => true,
            declaredType: (_, _) => null,
            declaredEver: (_, _) => false);

        Assert.Contains(report.Issues, i => i.Category == "missing-asset");
        Assert.Contains(report.Issues, i => i.Category == "field-not-in-class");
    }
}
