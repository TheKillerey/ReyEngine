using System.Globalization;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M496: writing a .bin as ritobin <c>#PROP_text</c>.
///
/// <para>The type spellings asserted here are ritobin's own, taken from
/// <c>ritobin_lib/src/ritobin/bin_types.hpp</c>, and the numeric ids match
/// <see cref="BinPropertyType"/> one for one — so this is a renaming, never a reinterpretation. Getting a
/// name wrong would produce text that looks right and that ritobin itself cannot read back.</para>
/// </summary>
public sealed class RitobinTextWriterTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static BinTree Tree(params BinTreeProperty[] properties) =>
        new(new[] { new BinTreeObject(H("Test/Object"), H("TestClass"), properties) }, Array.Empty<string>());

    /// <summary>Resolve only the names a test deliberately teaches it, so "unknown falls back to hex" is
    /// exercised rather than accidentally covered.</summary>
    private static Func<uint, string?> Names(params string[] known)
    {
        var map = known.ToDictionary(H, s => s);
        return h => map.TryGetValue(h, out var n) ? n : null;
    }

    [Fact]
    public void WritesTheHeaderAndTheThreeTopLevelFields()
    {
        var text = RitobinText.Write(Tree(new BinTreeU32(H("count"), 7)), Names("Test/Object", "TestClass", "count"));
        var lines = text.Split('\n');

        Assert.Equal("#PROP_text", lines[0]);
        Assert.Contains("type: string = \"PROP\"", text);
        Assert.Contains("version: u32 = 3", text);
        Assert.Contains("linked: list[string] = {", text);
        Assert.Contains("entries: map[hash,embed] = {", text);
        Assert.Contains("\"Test/Object\" = TestClass {", text);
        Assert.Contains("count: u32 = 7", text);
    }

    [Fact]
    public void UsesRitobinsTypeSpellingForEveryType()
    {
        // The four that differ most from LeagueToolkit's naming are the ones worth pinning: Container is
        // "list", UnorderedContainer is "list2", Struct is "pointer" and BitBool is "flag".
        Assert.Equal("list", RitobinText.TypeName(BinPropertyType.Container));
        Assert.Equal("list2", RitobinText.TypeName(BinPropertyType.UnorderedContainer));
        Assert.Equal("pointer", RitobinText.TypeName(BinPropertyType.Struct));
        Assert.Equal("embed", RitobinText.TypeName(BinPropertyType.Embedded));
        Assert.Equal("flag", RitobinText.TypeName(BinPropertyType.BitBool));
        Assert.Equal("file", RitobinText.TypeName(BinPropertyType.WadChunkLink));
        Assert.Equal("rgba", RitobinText.TypeName(BinPropertyType.Color));
        Assert.Equal("mtx44", RitobinText.TypeName(BinPropertyType.Matrix44));
        Assert.Equal("option", RitobinText.TypeName(BinPropertyType.Optional));

        // Every type must round-trip through the name table, or a bin using it becomes unwritable.
        foreach (var type in Enum.GetValues<BinPropertyType>())
        {
            Assert.True(RitobinText.TryParseType(RitobinText.TypeName(type), out var back),
                $"{type} has no parseable name");
            Assert.Equal(type, back);
        }
        Assert.False(RitobinText.TryParseType("nonsense", out _));
    }

    [Fact]
    public void FloatsAreWrittenInvariantRegardlessOfTheCurrentCulture()
    {
        // The developer's machine runs a German locale, where the default ToString gives "1,5" — which
        // reparses as two values and silently corrupts a vec4. This is the exact bug the project's
        // InvariantCulture rule exists for.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var text = RitobinText.Write(
                Tree(new BinTreeVector4(H("value"), new Vector4(0.5f, 1.5f, 2.25f, 1f))),
                Names("value"));

            Assert.Contains("value: vec4 = { 0.5, 1.5, 2.25, 1 }", text);
            Assert.DoesNotContain("0,5", text);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void UnresolvedNamesFallBackToHexAndResolvedHashValuesAreQuoted()
    {
        var text = RitobinText.Write(Tree(
            new BinTreeU32(0xdeadbeef, 1),                       // field name not in the database
            new BinTreeObjectLink(H("shader"), H("Shaders/Known"))),
            Names("shader", "Shaders/Known"));

        Assert.Contains("0xdeadbeef: u32 = 1", text);
        // A hash-valued property resolves to a QUOTED name, matching ritobin's own output.
        Assert.Contains("shader: link = \"Shaders/Known\"", text);

        // With nothing resolvable, the same link is a bare hash.
        var bare = RitobinText.Write(Tree(new BinTreeObjectLink(H("shader"), H("Shaders/Known"))), _ => null);
        Assert.Contains($"0x{H("shader"):x8}: link = 0x{H("Shaders/Known"):x8}", bare);
    }

    [Fact]
    public void ContainersCarryTheirElementTypeInBrackets()
    {
        var text = RitobinText.Write(Tree(
            new BinTreeContainer(H("paths"), BinPropertyType.String, new BinTreeProperty[]
            {
                new BinTreeString(0, "first"),
                new BinTreeString(0, "second"),
            }),
            new BinTreeUnorderedContainer(H("samplers"), BinPropertyType.U32, new BinTreeProperty[]
            {
                new BinTreeU32(0, 1),
            })),
            Names("paths", "samplers"));

        Assert.Contains("paths: list[string] = {", text);
        Assert.Contains("\"first\"", text);
        Assert.Contains("\"second\"", text);
        Assert.Contains("samplers: list2[u32] = {", text);
    }

    [Fact]
    public void EmbeddedStructsWriteTheirClassNameBeforeTheBody()
    {
        var text = RitobinText.Write(Tree(
            new BinTreeContainer(H("samplerValues"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("StaticMaterialShaderSamplerDef"), new BinTreeProperty[]
                {
                    new BinTreeString(H("TextureName"), "DiffuseTexture"),
                    new BinTreeU32(H("addressU"), 1),
                }),
            })),
            Names("samplerValues", "StaticMaterialShaderSamplerDef", "TextureName", "addressU"));

        Assert.Contains("samplerValues: list[embed] = {", text);
        Assert.Contains("StaticMaterialShaderSamplerDef {", text);
        Assert.Contains("TextureName: string = \"DiffuseTexture\"", text);
        Assert.Contains("addressU: u32 = 1", text);
    }

    [Fact]
    public void MapsWriteKeyEqualsValue()
    {
        var text = RitobinText.Write(Tree(
            new BinTreeMap(H("shaderMacros"), BinPropertyType.String, BinPropertyType.String,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeString(0, "NO_BAKED_LIGHTING"), new BinTreeString(0, "1")),
                })),
            Names("shaderMacros"));

        Assert.Contains("shaderMacros: map[string,string] = {", text);
        Assert.Contains("\"NO_BAKED_LIGHTING\" = \"1\"", text);
    }

    [Fact]
    public void OptionalWritesItsElementTypeAndIsEmptyWhenUnset()
    {
        var withValue = RitobinText.Write(Tree(
            new BinTreeOptional(H("icon"), new BinTreeString(0, "assets/icon.dds"))), Names("icon"));
        Assert.Contains("icon: option[string] = {", withValue);
        Assert.Contains("\"assets/icon.dds\"", withValue);

        // An empty option has no value to take a type from; ritobin still needs something in the brackets.
        var empty = RitobinText.Write(Tree(new BinTreeOptional(H("icon"), null!)), Names("icon"));
        Assert.Contains("icon: option[none] = {", empty);
    }

    [Fact]
    public void StringsAreEscapedSoTheyCanBeReadBack()
    {
        var text = RitobinText.Write(Tree(
            new BinTreeString(H("weird"), "a\"b\\c\nd\te")), Names("weird"));

        Assert.Contains(@"weird: string = ""a\""b\\c\nd\te""", text);
        // The raw newline must not survive into the output — it would end the line early.
        Assert.DoesNotContain("a\"b", text.Replace("\\\"", ""));
    }

    [Fact]
    public void BoolAndFlagBothWriteTrueFalse()
    {
        var text = RitobinText.Write(Tree(
            new BinTreeBool(H("blendEnable"), true),
            new BinTreeBitBool(H("packed"), false)),
            Names("blendEnable", "packed"));

        Assert.Contains("blendEnable: bool = true", text);
        Assert.Contains("packed: flag = false", text);
    }
}
