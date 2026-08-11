using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M418. Each rule in <see cref="ModShapeValidator"/> encodes one defect that shipped in the Map453 mod,
/// parsed cleanly in every tool, round-tripped byte-identically, and either crashed the game at map load
/// or rendered nothing. The rules exist so the next such mod is one dialog instead of two days.
///
/// <para><b>The control test is the important one.</b> Four confident hunches were wrong during that
/// investigation, so a validator that flags anything merely unusual is worse than none. A correctly
/// shaped material must produce ZERO issues, and that is asserted first.</para>
///
/// <para><b>Verified against the real files as well as these fixtures</b>, since a fixture only proves
/// the rule fires on what the fixture was built to contain. Run over the Map453 backups the rules report:
/// Riot's own <c>jade_container.materials.bin</c> 0 issues; the original crashing bin 708 (589
/// pointer-element, 117 half-blend-equation, 1 null-struct-form, 1 empty-container); the intermediate
/// bin that loaded but rendered nothing exactly 589 pointer-element and nothing else; the repaired bin
/// 0. On the geometry side the current mapgeo reports 0, the crash-2 backup reports the one
/// texcoord5-without-vertexdeform mesh, and the stripped-material backup reports its 3 missing
/// materials.</para>
/// </summary>
public class ModShapeValidatorTests
{
    private const string MaterialName = "Maps/KitPieces/Test/Materials/Terrain";
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static BinTreeProperty Element(BinPropertyType t, string cls, params BinTreeProperty[] props)
        => t == BinPropertyType.Embedded
            ? new BinTreeEmbedded(0, H(cls), props)
            : new BinTreeStruct(0, H(cls), props);

    /// <summary>A material of the shape Riot ships. Every argument moves ONE thing away from that shape,
    /// so a failing test names the defect it introduced.</summary>
    private static BinTree Bin(
        BinPropertyType elementType = BinPropertyType.Embedded,
        BinTreeProperty[]? extraPassProps = null,
        bool emptySwitches = false,
        string shader = "Shaders/StaticMesh/DefaultEnv")
    {
        var passProps = new List<BinTreeProperty> { new BinTreeObjectLink(H("shader"), H(shader)) };
        if (extraPassProps is not null) passProps.AddRange(extraPassProps);

        var pass = Element(elementType, "StaticMaterialPassDef", passProps.ToArray());
        var technique = Element(elementType, "StaticMaterialTechniqueDef",
            new BinTreeString(H("name"), "Default"),
            new BinTreeContainer(H("passes"), elementType, new[] { pass }));

        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("name"), MaterialName),
            new BinTreeContainer(H("samplerValues"), elementType, new[]
            {
                Element(elementType, "StaticMaterialShaderSamplerDef",
                    new BinTreeString(H("samplerName"), "DiffuseTexture"),
                    new BinTreeString(H("textureName"), "ASSETS/Maps/Test/diffuse.dds")),
            }),
            new BinTreeContainer(H("techniques"), elementType, new[] { technique }),
        };
        if (emptySwitches)
            props.Add(new BinTreeContainer(H("switches"), BinPropertyType.Embedded, Array.Empty<BinTreeProperty>()));

        return new BinTree(
            new[] { new BinTreeObject(H(MaterialName), H("StaticMaterialDef"), props) },
            Array.Empty<string>());
    }

    private static IReadOnlyList<BinIssue> Validate(BinTree tree)
        => ModShapeValidator.ValidateBin(tree, Array.Empty<byte>());

    // ---- the control ---------------------------------------------------------------------------

    [Fact]
    public void ACorrectlyShapedMaterialProducesNoIssues()
    {
        var issues = Validate(Bin());
        Assert.Empty(issues);
    }

    /// <summary>The same file after a real write/read cycle, so the rules are checked against what the
    /// library actually produces on the wire and not only against hand-built objects.</summary>
    [Fact]
    public void ACorrectlyShapedMaterialStaysCleanThroughARoundTrip()
    {
        using var ms = new MemoryStream();
        Bin().Write(ms);
        byte[] raw = ms.ToArray();

        var reread = new BinTree(new MemoryStream(raw, writable: false));
        Assert.Empty(ModShapeValidator.ValidateBin(reread, raw));
    }

    // ---- rule 3: container element type --------------------------------------------------------

    /// <summary>The defect that made 117 ported terrain materials invisible: every value correct, every
    /// container holding pointer elements instead of embedded ones.</summary>
    [Fact]
    public void PointerElementsAreReportedForEveryMaterialContainer()
    {
        var issues = Validate(Bin(BinPropertyType.Struct));
        var pointer = issues.Where(i => i.Category == "pointer-element").ToList();

        // samplerValues, techniques, and the nested passes container
        Assert.Equal(3, pointer.Count);
        Assert.All(pointer, i => Assert.Equal(MaterialName, i.ObjectName));
        Assert.Contains(pointer, i => i.Detail.Contains("samplerValues"));
        Assert.Contains(pointer, i => i.Detail.Contains("techniques"));
        Assert.Contains(pointer, i => i.Detail.Contains("passes"));
    }

    /// <summary>Unordered containers (wire type 0x81) must be checked too - real materials use them for
    /// paramValues, and the rule matching only the ordered type would silently skip them.</summary>
    [Fact]
    public void UnorderedContainersAreCheckedAsWell()
    {
        var tree = Bin();
        var material = tree.Objects.Values.Single();
        material.Properties[H("paramValues")] = new BinTreeUnorderedContainer(
            H("paramValues"), BinPropertyType.Struct, new[]
            {
                Element(BinPropertyType.Struct, "StaticMaterialShaderParamDef",
                    new BinTreeString(H("name"), "TintColor")),
            });

        Assert.IsAssignableFrom<BinTreeContainer>(material.Properties[H("paramValues")]);
        var issue = Assert.Single(Validate(tree), i => i.Detail.Contains("paramValues"));
        Assert.Equal("pointer-element", issue.Category);
    }

    /// <summary>An EMPTY container of the wrong element type is not reported as a pointer defect - there
    /// are no elements for the game to mis-bind, and the empty-container rule already covers it. Keeping
    /// these separate is what stops one mistake being reported twice.</summary>
    [Fact]
    public void AnEmptyContainerIsNotAlsoReportedAsAPointerDefect()
    {
        var tree = Bin();
        var material = tree.Objects.Values.Single();
        material.Properties[H("switches")] = new BinTreeContainer(
            H("switches"), BinPropertyType.Struct, Array.Empty<BinTreeProperty>());

        var issue = Assert.Single(Validate(tree));
        Assert.Equal("empty-container", issue.Category);
    }

    // ---- rule 2: empty containers --------------------------------------------------------------

    [Fact]
    public void AnEmptyContainerIsReported()
    {
        var issue = Assert.Single(Validate(Bin(emptySwitches: true)));
        Assert.Equal("empty-container", issue.Category);
        Assert.Equal(MaterialName, issue.ObjectName);
    }

    /// <summary>The rule fires on any class, not just materials - an empty MapBakeProperties container is
    /// what crashed the second Map453 build.</summary>
    [Fact]
    public void AnEmptyContainerIsReportedOutsideMaterialsToo()
    {
        var tree = new BinTree(new[]
        {
            new BinTreeObject(H("Maps/Test/Bake"), H("MapBakeProperties"), new BinTreeProperty[]
            {
                new BinTreeContainer(H("qualities"), BinPropertyType.Embedded, Array.Empty<BinTreeProperty>()),
            }),
        }, Array.Empty<string>());

        Assert.Equal("empty-container", Assert.Single(Validate(tree)).Category);
    }

    // ---- rule 4: blend factors move in pairs ---------------------------------------------------

    [Theory]
    [InlineData("srcColorBlendFactor", "colour", "src")]
    [InlineData("srcAlphaBlendFactor", "alpha", "src")]
    [InlineData("dstColorBlendFactor", "colour", "dst")]
    [InlineData("dstAlphaBlendFactor", "alpha", "dst")]
    public void HalfABlendEquationIsReported(string authored, string half, string pair)
    {
        var issue = Assert.Single(Validate(Bin(extraPassProps: new BinTreeProperty[]
        {
            new BinTreeBool(H("blendEnable"), true),
            new BinTreeU32(H(authored), 2u),
        })));

        Assert.Equal("half-blend-equation", issue.Category);
        Assert.Contains(half, issue.Detail);
        Assert.Contains($"the {pair} blend", issue.Detail);
    }

    [Fact]
    public void BothHalvesOfBothPairsIsClean()
    {
        Assert.Empty(Validate(Bin(extraPassProps: new BinTreeProperty[]
        {
            new BinTreeBool(H("blendEnable"), true),
            new BinTreeU32(H("srcColorBlendFactor"), 2u),
            new BinTreeU32(H("srcAlphaBlendFactor"), 2u),
            new BinTreeU32(H("dstColorBlendFactor"), 3u),
            new BinTreeU32(H("dstAlphaBlendFactor"), 3u),
        })));
    }

    /// <summary>With blending OFF the factors are inert, and half a pair is not a defect. Riot ships
    /// plenty of those, so reporting them would be the crying-wolf failure this validator must avoid.</summary>
    [Fact]
    public void HalfAPairIsNotReportedWhenBlendingIsOff()
    {
        Assert.Empty(Validate(Bin(extraPassProps: new BinTreeProperty[]
        {
            new BinTreeBool(H("blendEnable"), false),
            new BinTreeU32(H("dstColorBlendFactor"), 3u),
        })));
    }

    // ---- rule 1: long-form nullable structs ----------------------------------------------------

    /// <summary>A 0x82 struct is NULLABLE: class hash 0 means null and nothing follows. The long form adds
    /// six bytes the game's reader does not consume, so it misreads the rest of the container and crashes
    /// at map load. This is a wire-level question, which is why the rule needs the raw bytes.</summary>
    [Fact]
    public void ALongFormNullStructIsReported()
    {
        byte[] broken = NullStructBin(longForm: true);
        // the strict reader rejects these bytes outright, which is the premise: the tree here is the
        // repaired reading, and the rule answers from the raw bytes rather than from the tree
        Assert.ThrowsAny<Exception>(() => new BinTree(new MemoryStream(broken, writable: false)));

        var issue = Assert.Single(
            ModShapeValidator.ValidateBin(SafeBinTree.Parse(broken), broken),
            i => i.Category == "null-struct-form");

        Assert.Equal("(file)", issue.ObjectName);
        Assert.Contains("crashes at map load", issue.Detail);
    }

    [Fact]
    public void TheCanonicalNullFormIsClean()
    {
        byte[] raw = NullStructBin(longForm: false);
        Assert.Empty(ModShapeValidator.ValidateBin(new BinTree(new MemoryStream(raw, false)), raw));
    }

    /// <summary>One object holding a map with a null struct entry, in either encoding.</summary>
    private static byte[] NullStructBin(bool longForm)
    {
        var entries = new MemoryStream();
        var w = new BinaryWriter(entries);
        w.Write(0xAAAA0001u);
        w.Write(0u);                                   // class hash 0 == null
        if (longForm) { w.Write(2u); w.Write((ushort)0); }   // the six bytes the format says are NOT here
        w.Flush();
        byte[] entryBytes = entries.ToArray();

        var body = new MemoryStream();
        var b = new BinaryWriter(body);
        b.Write(H("items"));
        b.Write((byte)0x86);                           // Map
        b.Write((byte)0x11);                           // key: Hash
        b.Write((byte)0x82);                           // value: struct (NULLABLE)
        b.Write((uint)(entryBytes.Length + 4));        // size covers count + entries
        b.Write(1u);
        b.Write(entryBytes);
        b.Flush();
        byte[] bodyBytes = body.ToArray();

        var file = new MemoryStream();
        var f = new BinaryWriter(file);
        f.Write(new[] { 'P', 'R', 'O', 'P' });
        f.Write(3u);                                   // version
        f.Write(0u);                                   // dependency count
        f.Write(1u);                                   // object count
        f.Write(H("MapPlaceableContainer"));           // class list
        f.Write((uint)(bodyBytes.Length + 2 + 4));     // object size: propCount + path hash + body
        f.Write(H("Maps/Test/Placeables"));
        f.Write((ushort)1);
        f.Write(bodyBytes);
        f.Flush();
        return file.ToArray();
    }

    // ---- cross-file: the mapgeo lookup ---------------------------------------------------------

    /// <summary>The lookup is what lets the geometry rules ask "does the bin define this material, and on
    /// which shader" - and its null answer IS the missing-material finding, so it is worth pinning.</summary>
    [Fact]
    public void MaterialShaderLookupResolvesDefinedMaterialsAndReturnsNullForTheRest()
    {
        var lookup = ModShapeValidator.MaterialShaderLookup(
            new[] { Bin(shader: "Shaders/StaticMesh/VertexDeform") },
            h => h == H("Shaders/StaticMesh/VertexDeform") ? "Shaders/StaticMesh/VertexDeform" : null);

        Assert.Equal("Shaders/StaticMesh/VertexDeform", lookup(MaterialName));
        Assert.Null(lookup("Maps/KitPieces/Test/Materials/DoesNotExist"));
    }

    /// <summary>Unreadable geometry is reported, not thrown - the validator runs over whatever a mod
    /// contains, including files that are already broken.</summary>
    [Fact]
    public void UnreadableGeometryIsReportedRatherThanThrown()
    {
        var issue = Assert.Single(ModShapeValidator.ValidateMapGeo(
            "broken.mapgeo", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, _ => null));
        Assert.Equal("mapgeo-unreadable", issue.Category);
    }

    // ---- M430: the shader contract ---------------------------------------------------------------

    private static ReyEngine.Formats.Shaders.LeagueShaderDef Shader(
        string name, string[] parameters, string[] switches, string[] textures)
        => new(name, "StaticMesh",
            textures.Select(t => new ReyEngine.Formats.Shaders.ShaderTextureDef(t, "")).ToList(),
            parameters.Select(p => new ReyEngine.Formats.Shaders.ShaderParamDef(p, 0, 0, 0, 0)).ToList(),
            switches.ToList());

    /// <summary>
    /// A parameter the linked shader does not declare. Measured over every shipped WAD: of 31,954
    /// materials that set any paramValue, ZERO set one their shader does not declare. This is what a
    /// shader swap used to leave behind - a TintColor from DefaultEnv_Flat stranded on
    /// Mantis_Env_Baked_PBR, whose 39 parameters do not include it, and the map crashed at load with
    /// nothing useful in Riot's log.
    /// </summary>
    [Fact]
    public void AParameterTheShaderDoesNotDeclareIsReported()
    {
        var tree = Bin();
        var material = tree.Objects.Values.Single();
        material.Properties[H("paramValues")] = new BinTreeUnorderedContainer(
            H("paramValues"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                Element(BinPropertyType.Embedded, "StaticMaterialShaderParamDef",
                    new BinTreeString(H("name"), "TintColor")),
            });

        var shader = Shader("Shaders/StaticMesh/Mantis_Env_Baked_PBR",
            new[] { "WaterFlowTiling", "CausticsStrength" }, Array.Empty<string>(), Array.Empty<string>());
        var issues = ModShapeValidator.ValidateAgainstShaders(
            tree, h => h == H("Shaders/StaticMesh/DefaultEnv") ? shader : null);

        var issue = Assert.Single(issues, i => i.Category == "undeclared-parameter");
        Assert.Contains("TintColor", issue.Detail);
        Assert.Contains("0 of 31,954", issue.Detail);
    }

    /// <summary>A declared parameter is fine, and must not be reported.</summary>
    [Fact]
    public void ADeclaredParameterIsClean()
    {
        var tree = Bin();
        tree.Objects.Values.Single().Properties[H("paramValues")] = new BinTreeUnorderedContainer(
            H("paramValues"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                Element(BinPropertyType.Embedded, "StaticMaterialShaderParamDef",
                    new BinTreeString(H("name"), "WaterFlowTiling")),
            });

        var shader = Shader("S", new[] { "WaterFlowTiling" }, Array.Empty<string>(), Array.Empty<string>());
        Assert.DoesNotContain(
            ModShapeValidator.ValidateAgainstShaders(tree, _ => shader),
            i => i.Category == "undeclared-parameter");
    }

    /// <summary>Every one of the 27,295 shipped materials on a shader that declares staticSwitches
    /// carries a switches container. The Mantis test material had none.</summary>
    [Fact]
    public void AMissingSwitchesContainerIsReportedWhenTheShaderDeclaresSwitches()
    {
        var shader = Shader("S", Array.Empty<string>(), new[] { "USE_WATER", "USE_VOID" }, Array.Empty<string>());
        var issue = Assert.Single(
            ModShapeValidator.ValidateAgainstShaders(Bin(), _ => shader),
            i => i.Category == "missing-switches");
        Assert.Contains("USE_VOID", issue.Detail);

        // and a shader with no switches must not produce the finding
        var plain = Shader("S", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        Assert.DoesNotContain(
            ModShapeValidator.ValidateAgainstShaders(Bin(), _ => plain),
            i => i.Category == "missing-switches");
    }

    /// <summary>A sampler binding the shader never declares is dead weight the shader cannot read -
    /// the other half of what a shader swap strands.</summary>
    [Fact]
    public void ASamplerTheShaderDoesNotDeclareIsReported()
    {
        // real materials name the sampler with TextureName - 100% of 102,406 shipped samplers do
        var tree = Bin();
        tree.Objects.Values.Single().Properties[H("samplerValues")] = new BinTreeUnorderedContainer(
            H("samplerValues"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                Element(BinPropertyType.Embedded, "StaticMaterialShaderSamplerDef",
                    new BinTreeString(H("TextureName"), "LeftoverNormal"),
                    new BinTreeString(H("texturePath"), "ASSETS/x.tex")),
            });

        var shader = Shader("S", Array.Empty<string>(), Array.Empty<string>(), new[] { "BAKED_DIFFUSE_TEXTURE" });
        var issue = Assert.Single(
            ModShapeValidator.ValidateAgainstShaders(tree, _ => shader),
            i => i.Category == "undeclared-sampler");
        Assert.Contains("LeftoverNormal", issue.Detail);
    }

    /// <summary>No shader definition means no contract to check against, and nothing is claimed.</summary>
    [Fact]
    public void AnUnknownShaderProducesNoContractFindings()
        => Assert.Empty(ModShapeValidator.ValidateAgainstShaders(Bin(), _ => null));
}
