using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M503: the material audit behind the map-wide material browser.
///
/// <para>Every check here corresponds to something that actually went wrong on a real ported map between
/// M486 and M502. The property that matters most is RESTRAINT: a check with no way to answer must be
/// SKIPPED, not guessed. A browser that invents warnings trains the user to ignore it, which is worse than
/// having no browser.</para>
/// </summary>
public sealed class MapMaterialAuditTests
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>A material with a shader, one sampler and optionally a TintColor.</summary>
    private static MaterialBinding Material(string name, string shader, string? texture = "assets/t.tex",
        float? tint = null, params (string Macro, string Value)[] macros)
    {
        var props = new List<BinTreeProperty>
        {
            new BinTreeString(H("name"), name),
            new BinTreeContainer(H("techniques"), BinPropertyType.Embedded, new BinTreeProperty[]
            {
                new BinTreeEmbedded(0, H("StaticMaterialTechniqueDef"), new BinTreeProperty[]
                {
                    new BinTreeContainer(H("passes"), BinPropertyType.Embedded, new BinTreeProperty[]
                    {
                        new BinTreeEmbedded(0, H("StaticMaterialPassDef"), new BinTreeProperty[]
                        {
                            new BinTreeObjectLink(H("shader"), H(shader)),
                        }),
                    }),
                }),
            }),
        };

        if (texture is not null)
            props.Add(new BinTreeUnorderedContainer(H("samplerValues"), BinPropertyType.Embedded,
                new BinTreeProperty[]
                {
                    new BinTreeEmbedded(0, 0x0904b150, new BinTreeProperty[]
                    {
                        new BinTreeString(H("TextureName"), "DiffuseTexture"),
                        new BinTreeString(H("texturePath"), texture),
                    }),
                }));

        if (tint is { } t)
            props.Add(new BinTreeUnorderedContainer(H("paramValues"), BinPropertyType.Embedded,
                new BinTreeProperty[]
                {
                    new BinTreeEmbedded(0, 0xde480eef, new BinTreeProperty[]
                    {
                        new BinTreeString(H("name"), "TintColor"),
                        new BinTreeVector4(H("value"), new Vector4(t, t, t, 1f)),
                    }),
                }));

        if (macros.Length > 0)
            props.Add(new BinTreeMap(H("shaderMacros"), BinPropertyType.String, BinPropertyType.String,
                macros.Select(m => new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                    new BinTreeString(0, m.Macro), new BinTreeString(0, m.Value))).ToList()));

        using var ms = new MemoryStream();
        new BinTree(new[] { new BinTreeObject(H(name), H("StaticMaterialDef"), props) },
            Array.Empty<string>()).Write(ms);

        // Round-trip through the real parser so RenderShader, Slots and Macros are populated the way the
        // app sees them — a hand-built MaterialBinding would test a shape the app never encounters.
        //
        // The CLASS hash must resolve too: a material with no samplerValues is only kept when the parser
        // can see it is a StaticMaterialDef (MaterialDocument.cs:217). In the app that name comes from the
        // hash database; a fixture that omits it silently loses exactly the texture-less materials this
        // audit is meant to find.
        var names = new Dictionary<uint, string>
        {
            [H(shader)] = shader,
            [H("StaticMaterialDef")] = "StaticMaterialDef",
        };
        return MaterialDocument.Parse(ms.ToArray(), h => names.TryGetValue(h, out var n) ? n : null)
            .Materials.Single();
    }

    private static Dictionary<string, (int, int)> Usage(params (string Name, int Meshes)[] rows) =>
        rows.ToDictionary(r => r.Name, r => (r.Meshes, r.Meshes), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AMeshNamingAMaterialTheBinDoesNotHaveIsReported()
    {
        // This is the white mesh, and it is invisible if you only list the materials that EXIST — which is
        // exactly what the old per-material property grid did.
        var rows = MapMaterialAudit.Audit(
            new[] { Material("Have", "Shaders/A") },
            Usage(("Have", 2), ("Gone", 5)));

        var missing = Assert.Single(rows, r => r.Name == "Gone");
        var finding = Assert.Single(missing.Findings);
        Assert.Equal(MaterialIssue.MissingMaterial, finding.Issue);
        Assert.Contains("5 mesh", finding.Detail);
        Assert.Equal(5, missing.MeshCount);
    }

    [Fact]
    public void ChecksWithNoWayToAnswerAreSkippedRatherThanGuessed()
    {
        // No context at all: the only findings possible are the ones answerable from the material itself.
        var rows = MapMaterialAudit.Audit(
            new[] { Material("M", "Shaders/A", texture: "assets/definitely/missing.tex", tint: 1f,
                             macros: ("NO_BAKED_LIGHTING", "1")) },
            Usage(("M", 1)));

        var row = Assert.Single(rows);
        Assert.DoesNotContain(row.Findings, f => f.Issue == MaterialIssue.UnresolvedTexture);
        Assert.DoesNotContain(row.Findings, f => f.Issue == MaterialIssue.UncookedPermutation);
        Assert.DoesNotContain(row.Findings, f => f.Issue == MaterialIssue.InertMacro);
        Assert.DoesNotContain(row.Findings, f => f.Issue == MaterialIssue.NonNeutralTint);
        Assert.Empty(row.Findings);
    }

    [Fact]
    public void InertAndUncookedMacrosAreDistinguished()
    {
        // The two failures are opposites: an undeclared axis is IGNORED by the client (misleading), a
        // declared-but-uncooked one makes the client fail to compile the shader (M486). Reporting them the
        // same way would make the dangerous one look routine.
        var inert = Material("Inert", "Shaders/FourBlend", macros: ("NO_BAKED_LIGHTING", "1"));
        var uncooked = Material("Uncooked", "Shaders/AlphaTest", macros: ("NO_BAKED_LIGHTING", "1"));

        var rows = MapMaterialAudit.Audit(new[] { inert, uncooked },
            Usage(("Inert", 1), ("Uncooked", 1)),
            new MaterialAuditContext(
                ShaderDeclaresMacro: (shader, _) => shader != "Shaders/FourBlend",
                CanSetMacro: (m, _) => m.Name != "Uncooked"));

        Assert.Contains(rows.Single(r => r.Name == "Inert").Findings,
            f => f.Issue == MaterialIssue.InertMacro && f.Detail.Contains("ignores"));
        Assert.Contains(rows.Single(r => r.Name == "Uncooked").Findings,
            f => f.Issue == MaterialIssue.UncookedPermutation);

        // An ignored macro must NOT also be reported as uncooked — asking whether an ignored macro is
        // cooked is a question with no meaning, and two findings for one cause is noise.
        Assert.DoesNotContain(rows.Single(r => r.Name == "Inert").Findings,
            f => f.Issue == MaterialIssue.UncookedPermutation);
    }

    [Fact]
    public void TintIsComparedAgainstTheShadersOwnDefaultNotAHardcodedNumber()
    {
        // M500: neutral is per-shader. DefaultEnv_Flat's overlay identity is 0.5019608, and 1.0 there is a
        // ~2x brightener — but 1.0 is genuinely neutral for a shader that declares it.
        var rows = MapMaterialAudit.Audit(
            new[]
            {
                Material("Overlay_Wrong", "Shaders/Overlay", tint: 1f),
                Material("Overlay_Right", "Shaders/Overlay", tint: 0.5019608f),
                Material("Multiply_Right", "Shaders/Multiply", tint: 1f),
            },
            Usage(("Overlay_Wrong", 1), ("Overlay_Right", 1), ("Multiply_Right", 1)),
            new MaterialAuditContext(ShaderTintDefault: shader => shader == "Shaders/Overlay"
                ? new Vector4(0.5019608f, 0.5019608f, 0.5019608f, 0f)
                : Vector4.One));

        var wrong = Assert.Single(rows.Single(r => r.Name == "Overlay_Wrong").Findings,
            f => f.Issue == MaterialIssue.NonNeutralTint);
        Assert.Contains("BRIGHTENER", wrong.Detail);
        Assert.DoesNotContain(rows.Single(r => r.Name == "Overlay_Right").Findings,
            f => f.Issue == MaterialIssue.NonNeutralTint);
        Assert.DoesNotContain(rows.Single(r => r.Name == "Multiply_Right").Findings,
            f => f.Issue == MaterialIssue.NonNeutralTint);
    }

    [Fact]
    public void UnresolvedTexturesNameTheSamplerAndThePath()
    {
        var rows = MapMaterialAudit.Audit(
            new[] { Material("M", "Shaders/A", texture: "assets/gone.tex") },
            Usage(("M", 1)),
            new MaterialAuditContext(TextureExists: p => !p.Contains("gone")));

        var f = Assert.Single(rows.Single().Findings, x => x.Issue == MaterialIssue.UnresolvedTexture);
        Assert.Contains("DiffuseTexture", f.Detail);
        Assert.Contains("assets/gone.tex", f.Detail);
    }

    [Fact]
    public void UnusedAndTextureLessMaterialsAreReported()
    {
        var rows = MapMaterialAudit.Audit(
            new[] { Material("Orphan", "Shaders/A", texture: null) },
            Usage());

        var row = Assert.Single(rows);
        Assert.Contains(row.Findings, f => f.Issue == MaterialIssue.Unused);
        Assert.Contains(row.Findings, f => f.Issue == MaterialIssue.NoTextures);
        Assert.Equal(0, row.MeshCount);
    }

    [Fact]
    public void UsageCountsDistinctMeshesNotSubmeshes()
    {
        // One mesh can contribute several submeshes with the same material; counting submeshes would
        // overstate how much of the map a material actually covers.
        var usage = MapMaterialAudit.UsageFrom(new[]
        {
            ("Mat", 1), ("Mat", 1), ("Mat", 2), ("Other", 3),
        });

        Assert.Equal((2, 3), usage["Mat"]);
        Assert.Equal((1, 1), usage["Other"]);
    }

    [Fact]
    public void WorstIssueRanksTheRowForSorting()
    {
        var rows = MapMaterialAudit.Audit(
            new[] { Material("M", "Shaders/A", texture: null) },
            Usage());

        // NoTextures outranks Unused, so the row sorts by the thing most worth looking at.
        Assert.Equal(MaterialIssue.NoTextures, rows.Single().Worst);
        Assert.True(MaterialIssue.MissingMaterial < MaterialIssue.Unused,
            "issue order is the ranking — the ones that break rendering must come first");
    }

    [Fact]
    public void AMeshWithNoLightmapUvUnderABakedShaderIsReported()
    {
        // M509: the lightmap UV lives on the MESH and the decision to sample it lives in the MATERIAL, so
        // neither file is wrong on its own — which is exactly why this survives every per-file check and
        // shows up only as geometry that looks wrong in game.
        var rows = MapMaterialAudit.Audit(
            new[] { Material("Baked", "Shaders/Flat"), Material("Unlit", "Shaders/Flat",
                        macros: ("NO_BAKED_LIGHTING", "1")) },
            Usage(("Baked", 4), ("Unlit", 2)),
            new MaterialAuditContext(LightmapUvCoverage: m => m.Name == "Baked" ? (0, 4) : (0, 2)));

        var finding = Assert.Single(rows.Single(r => r.Name == "Baked").Findings,
            f => f.Issue == MaterialIssue.MissingLightmapUv);
        Assert.Contains("4 mesh", finding.Detail);
        Assert.Contains("NO_BAKED_LIGHTING", finding.Detail);

        // A material that already opts out of baked lighting has nothing to sample and nothing to report.
        Assert.DoesNotContain(rows.Single(r => r.Name == "Unlit").Findings,
            f => f.Issue == MaterialIssue.MissingLightmapUv);
    }

    [Fact]
    public void AMaterialSharedByBothKindsOfMeshSaysSoRatherThanSuggestingTheUnsafeFix()
    {
        // Marking it unlit would unlight the meshes that are currently fine. On the real map this split
        // was clean — 0 shared materials — but the advice must not depend on that luck.
        var rows = MapMaterialAudit.Audit(
            new[] { Material("Shared", "Shaders/Flat") },
            Usage(("Shared", 10)),
            new MaterialAuditContext(LightmapUvCoverage: _ => (7, 3)));

        var finding = Assert.Single(rows.Single().Findings, f => f.Issue == MaterialIssue.MissingLightmapUv);
        Assert.Contains("3 of 10", finding.Detail);
        Assert.Contains("their own material", finding.Detail);
        Assert.DoesNotContain("Set NO_BAKED_LIGHTING on it", finding.Detail);
    }
}
