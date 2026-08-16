using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M507: the container's own wire form.
///
/// <para>Container (0x80, "list") and UnorderedContainer (0x81, "list2") parse identically here, round-trip
/// identically, and diff identically — and the CLIENT skips the property when the tag disagrees with the
/// schema. Nothing about the values is wrong; the game just reads a material with the field missing.</para>
///
/// <para>Measured consequence, from a real r3d log: the editor wrote <c>switches</c> as a plain Container,
/// the client skipped it, MULTIPLY_ALPHA fell back to the shader default of 0, and the material's
/// PREMULTIPLIED_ALPHA=1 then asked DefaultEnv_Flat_AlphaTest for a permutation that does not exist —
/// every one of that shader's 256 cooked PREMULTIPLIED_ALPHA permutations also carries MULTIPLY_ALPHA=1.
/// The map failed to load.</para>
/// </summary>
public sealed class MaterialContainerShapeTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private static readonly uint MaterialClass = H("StaticMaterialDef");

    private static BinTreeProperty Switch(string name, bool on) =>
        new BinTreeEmbedded(0, H("StaticMaterialSwitchDef"), new BinTreeProperty[]
        {
            new BinTreeString(H("name"), name),
            new BinTreeBool(H("on"), on),
        });

    private static BinTree Tree(BinTreeProperty switches) =>
        new(new[]
        {
            new BinTreeObject(H("Mat"), MaterialClass, new BinTreeProperty[]
            {
                new BinTreeString(H("name"), "Mat"),
                switches,
            }),
        }, Array.Empty<string>());

    [Fact]
    public void RiotsFormIsUnorderedForSwitchesAndOrderedForTechniques()
    {
        // Censused over the 18 shipped map wads: switches 6,561/6,561 UnorderedContainer,
        // samplerValues 10,917/10,917 UnorderedContainer, techniques 10,982/10,982 Container.
        Assert.True(MaterialContainerShape.IsUnordered("switches"));
        Assert.True(MaterialContainerShape.IsUnordered("samplerValues"));
        Assert.False(MaterialContainerShape.IsUnordered("techniques"));

        // A field with no measurement gets no opinion — the same restraint the audit uses.
        Assert.Null(MaterialContainerShape.IsUnordered("childTechniques"));
    }

    [Fact]
    public void AddingTheFirstSwitchWritesTheFormRiotShips()
    {
        // The regression itself: MaterialBinding.AddSwitch created the container, and it created the
        // wrong one. Everything downstream looked correct.
        using var ms = new MemoryStream();
        new BinTree(new[]
        {
            new BinTreeObject(H("Mat"), MaterialClass, new BinTreeProperty[]
            {
                new BinTreeString(H("name"), "Mat"),
                new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded,
                    new BinTreeProperty[]
                    {
                        new BinTreeEmbedded(0, 0x0904b150, new BinTreeProperty[]
                        {
                            new BinTreeString(H("TextureName"), "DiffuseTexture"),
                            new BinTreeString(H("texturePath"), "assets/a.tex"),
                        }),
                    }),
            }),
        }, Array.Empty<string>()).Write(ms);

        var names = new Dictionary<uint, string> { [MaterialClass] = "StaticMaterialDef" };
        var material = MaterialDocument.Parse(ms.ToArray(), h => names.TryGetValue(h, out var n) ? n : null)
            .Materials.Single();

        Assert.NotNull(material.AddSwitch("MULTIPLY_ALPHA"));
        Assert.Empty(material.MistaggedContainers);
        Assert.True(material.ClientVisibleSwitches.ContainsKey("MULTIPLY_ALPHA"));
    }

    [Fact]
    public void AMistaggedContainerIsReportedAndTheSwitchesBecomeInvisible()
    {
        using var ms = new MemoryStream();
        Tree(new BinTreeContainer(H("switches"), BinPropertyType.Embedded,
            new[] { Switch("MULTIPLY_ALPHA", true) })).Write(ms);
        var names = new Dictionary<uint, string> { [MaterialClass] = "StaticMaterialDef" };
        var material = MaterialDocument.Parse(ms.ToArray(), h => names.TryGetValue(h, out var n) ? n : null)
            .Materials.Single();

        Assert.Equal(new[] { "switches" }, material.MistaggedContainers);

        // We can read it. The game cannot. Every permutation check has to use the second reading, or it
        // answers a question about a material the client never loads.
        Assert.True(material.Switches["MULTIPLY_ALPHA"]);
        Assert.Empty(material.ClientVisibleSwitches);
    }

    [Fact]
    public void TheValidatorCallsItOutWithTheEvidence()
    {
        using var ms = new MemoryStream();
        Tree(new BinTreeContainer(H("switches"), BinPropertyType.Embedded,
            new[] { Switch("MULTIPLY_ALPHA", true) })).Write(ms);
        byte[] bytes = ms.ToArray();

        var issues = ModShapeValidator.ValidateBin(SafeBinTree.Parse(bytes), bytes, _ => null);
        var issue = Assert.Single(issues, i => i.Category == "container-wire-form");
        Assert.Contains("switches", issue.Detail);
        Assert.Contains("UnorderedContainer", issue.Detail);
        Assert.Contains("6,561", issue.Detail);
    }

    [Fact]
    public void RepairFixesTheTagAndKeepsEveryValue()
    {
        using var ms = new MemoryStream();
        Tree(new BinTreeContainer(H("switches"), BinPropertyType.Embedded,
            new[] { Switch("MULTIPLY_ALPHA", true), Switch("USE_MASK", false) })).Write(ms);
        byte[] before = ms.ToArray();

        var tree = SafeBinTree.Parse(before);
        var repaired = MaterialContainerShape.Repair(tree, MaterialClass);
        Assert.Single(repaired);
        Assert.Contains("switches", repaired[0]);

        using var outMs = new MemoryStream();
        tree.Write(outMs);
        byte[] after = outMs.ToArray();

        var names = new Dictionary<uint, string> { [MaterialClass] = "StaticMaterialDef" };
        var material = MaterialDocument.Parse(after, h => names.TryGetValue(h, out var n) ? n : null)
            .Materials.Single();

        Assert.Empty(material.MistaggedContainers);
        Assert.Empty(ModShapeValidator.ValidateBin(SafeBinTree.Parse(after), after, _ => null)
            .Where(i => i.Category == "container-wire-form"));

        // Values, order and on/off survive: this changes the tag, not the content.
        Assert.Equal(2, material.ClientVisibleSwitches.Count);
        Assert.True(material.ClientVisibleSwitches["MULTIPLY_ALPHA"]);
        Assert.False(material.ClientVisibleSwitches["USE_MASK"]);
    }

    [Fact]
    public void RepairIsANoOpOnAFileThatIsAlreadyRight()
    {
        using var ms = new MemoryStream();
        Tree(new BinTreeUnorderedContainer(H("switches"), BinPropertyType.Embedded,
            new[] { Switch("MULTIPLY_ALPHA", true) })).Write(ms);
        byte[] before = ms.ToArray();

        var tree = SafeBinTree.Parse(before);
        Assert.Empty(MaterialContainerShape.Repair(tree, MaterialClass));

        using var outMs = new MemoryStream();
        tree.Write(outMs);
        Assert.Equal(before, outMs.ToArray());     // byte-exact: nothing to do means nothing changed
    }
}
