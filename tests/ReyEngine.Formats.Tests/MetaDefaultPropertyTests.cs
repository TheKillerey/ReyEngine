using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Formats.Meta;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M370: synthesising a bin property from the schema's authored default. This is a WRITE path into a real
/// mod's .bin, so the refusals matter at least as much as the successes and are tested as hard.
/// Every default string here is copied verbatim from the shipping lol-meta-classes dump.
/// </summary>
public class MetaDefaultPropertyTests
{
    private static BinTreeProperty Create(string type, string json)
    {
        Assert.True(MetaDefaultProperty.TryCreate(0x1234, type, json, out var p, out var reason),
            $"expected success, got: {reason}");
        Assert.NotNull(p);
        Assert.Null(reason);
        return p!;
    }

    [Fact]
    public void BuildsU8FromRealDefault()   // alphaRef = 5
    {
        var p = Assert.IsType<BinTreeU8>(Create("U8", "5"));
        Assert.Equal(5, p.Value);
        Assert.Equal(0x1234u, p.NameHash);
    }

    [Fact]
    public void BuildsF32() => Assert.Equal(0f, Assert.IsType<BinTreeF32>(Create("F32", "0.0")).Value);

    [Fact]
    public void BuildsI32FromNegativeDefault()   // audioParameterFlexID = -1
        => Assert.Equal(-1, Assert.IsType<BinTreeI32>(Create("I32", "-1")).Value);

    [Fact]
    public void BuildsU16()   // flags = 212
        => Assert.Equal(212, Assert.IsType<BinTreeU16>(Create("U16", "212")).Value);

    [Fact]
    public void BuildsBool()   // hudAnchorPositionFromWorldProjection = true
        => Assert.True(Assert.IsType<BinTreeBool>(Create("Bool", "true")).Value);

    /// <summary>Flag is the BIT-PACKED bool and a distinct wire type. Writing a BinTreeBool for it would
    /// produce a property the game reads at the wrong width.</summary>
    [Fact]
    public void FlagBuildsBitBoolNotBool()
    {
        var p = Create("Flag", "false");
        Assert.IsType<BinTreeBitBool>(p);
        Assert.IsNotType<BinTreeBool>(p);
        Assert.False(((BinTreeBitBool)p).Value);
    }

    [Fact]
    public void BuildsString()   // emissionMeshName = ""
        => Assert.Equal("", Assert.IsType<BinTreeString>(Create("String", "\"\"")).Value);

    /// <summary>Hash defaults are recorded as a hex STRING ("0x0"). Reading one as a number silently
    /// yields 0, which would look like it worked.</summary>
    [Fact]
    public void BuildsHashFromHexString()
    {
        Assert.Equal(0u, Assert.IsType<BinTreeHash>(Create("Hash", "\"0x0\"")).Value);
        Assert.Equal(0xdeadbeefu, Assert.IsType<BinTreeHash>(Create("Hash", "\"0xdeadbeef\"")).Value);
    }

    [Fact]
    public void BuildsVectors()
    {
        Assert.Equal(Vector2.Zero, Assert.IsType<BinTreeVector2>(Create("Vec2", "[0.0,0.0]")).Value);
        Assert.Equal(Vector3.Zero, Assert.IsType<BinTreeVector3>(Create("Vec3", "[0.0,0.0,0.0]")).Value);
        Assert.Equal(Vector4.One, Assert.IsType<BinTreeVector4>(Create("Vec4", "[1.0,1.0,1.0,1.0]")).Value);
    }

    // ---- refusals ----

    [Theory]
    [InlineData("Embed", "{\"constantValue\":[0.0,0.0,0.0],\"dynamics\":null}")]
    [InlineData("Pointer", "null")]
    [InlineData("Link", "\"0x0\"")]
    [InlineData("List", "[]")]
    [InlineData("List2", "[]")]
    [InlineData("Map", "{}")]
    [InlineData("Option", "0.0")]
    [InlineData("Mtx44", "[[1.0,0.0,0.0,0.0]]")]
    [InlineData("Color", "[1.0,1.0,1.0,1.0]")]
    public void RefusesTypesThatAreNotSelfContained(string type, string json)
    {
        Assert.False(MetaDefaultProperty.IsSupported(type));
        Assert.False(MetaDefaultProperty.TryCreate(1, type, json, out var p, out var reason));
        Assert.Null(p);
        Assert.NotNull(reason);
    }

    /// <summary>No default means no measured value, so nothing is written. Inventing a zero here would be
    /// asserting a game behaviour nobody checked.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RefusesWhenTheSchemaRecordsNoDefault(string? json)
    {
        Assert.False(MetaDefaultProperty.TryCreate(1, "F32", json, out var p, out var reason));
        Assert.Null(p);
        Assert.Contains("no default", reason);
    }

    [Fact]
    public void RefusesMalformedDefaultRatherThanWritingGarbage()
    {
        Assert.False(MetaDefaultProperty.TryCreate(1, "Vec3", "[1.0,2.0]", out var p, out var reason));
        Assert.Null(p);
        Assert.NotNull(reason);
    }

    [Fact]
    public void RefusesOutOfRangeIntegerRatherThanTruncating()
    {
        // 300 does not fit a U8. Truncating to 44 would be a silently wrong value in someone's mod.
        Assert.False(MetaDefaultProperty.TryCreate(1, "U8", "300", out var p, out var reason));
        Assert.Null(p);
        Assert.NotNull(reason);
    }

    [Fact]
    public void RefusesWrongJsonShape()
    {
        Assert.False(MetaDefaultProperty.TryCreate(1, "F32", "\"not a number\"", out _, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void DeclineReasonIsNullExactlyWhenCreatable()
    {
        Assert.Null(MetaDefaultProperty.DeclineReason("F32", "1.0"));
        Assert.NotNull(MetaDefaultProperty.DeclineReason("Embed", "{}"));
        Assert.NotNull(MetaDefaultProperty.DeclineReason("F32", null));
    }
}
