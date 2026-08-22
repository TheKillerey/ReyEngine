using System.Numerics;
using System.Text.RegularExpressions;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M550: rebuilding legacy decals as flat planes, one whole texture each.
/// </summary>
public sealed class LegacyDecalQuadTests
{
    /// <summary>A flat patch on the ground whose UV covers one tile, as two triangles.</summary>
    private static List<DecalSourceTriangle> FlatTile(float size = 100f, float y = 0f, int tileU = 0, int tileV = 0)
    {
        Vector3 P(float u, float v) => new((tileU + u) * size, y, (tileV + v) * size);
        Vector2 T(float u, float v) => new(tileU + u, tileV + v);
        // Wound counter-clockwise seen from above, so the patch faces +Y. Getting this backwards is not
        // a generator bug - it matches the source winding by design - but it makes the fixture describe a
        // decal facing into the ground, which no real port produces (Map2: 241 of 241 face up).
        return new List<DecalSourceTriangle>
        {
            new(P(0,0), P(0,1), P(1,1), T(0,0), T(0,1), T(1,1)),
            new(P(0,0), P(1,1), P(1,0), T(0,0), T(1,1), T(1,0)),
        };
    }

    [Fact]
    public void AFlatPatchBecomesOnePlaneCoveringItsWholeTexture()
    {
        var quads = LegacyDecalQuadGenerator.Generate(FlatTile(), lift: 0f, out int skipped);
        Assert.Equal(0, skipped);
        var quad = Assert.Single(quads);

        // The quad reproduces the patch's own footprint, because the UV covered exactly one tile.
        Assert.Equal(0f, quad.A.X, 3); Assert.Equal(0f, quad.A.Z, 3);
        Assert.Equal(100f, quad.C.X, 3); Assert.Equal(100f, quad.C.Z, 3);
    }

    [Fact]
    public void EachTileBecomesItsOwnPlane()
    {
        // The unit is the UV tile: the texture repeats once per tile, so a patch spanning three tiles is
        // three whole images and therefore three planes.
        var source = new List<DecalSourceTriangle>();
        source.AddRange(FlatTile(tileU: 0));
        source.AddRange(FlatTile(tileU: 1));
        source.AddRange(FlatTile(tileU: 2));

        var quads = LegacyDecalQuadGenerator.Generate(source, lift: 0f, out _);
        Assert.Equal(3, quads.Count);
        Assert.Equal(new[] { 0, 1, 2 }, quads.Select(q => q.TileU).OrderBy(x => x));
    }

    [Fact]
    public void ThePlaneIsLiftedAlongItsNormalNotBlindlyUpwards()
    {
        var flat = LegacyDecalQuadGenerator.Generate(FlatTile(y: 50f), lift: 4f, out _);
        var quad = Assert.Single(flat);
        Assert.Equal(54f, quad.A.Y, 3);
        Assert.True(quad.Normal.Y > 0.99f, $"a ground decal faces up, saw {quad.Normal}");
    }

    [Fact]
    public void APlaneFacesTheWayItsSourceDid()
    {
        // A fit can come out inverted, and an inverted decal is invisible under backface culling. The
        // generator compares against the source winding and flips the quad rather than the normal alone.
        var flipped = FlatTile().Select(t =>
            new DecalSourceTriangle(t.P0, t.P2, t.P1, t.T0, t.T2, t.T1)).ToList();

        var upward = LegacyDecalQuadGenerator.Generate(FlatTile(), lift: 0f, out _).Single();
        var downward = LegacyDecalQuadGenerator.Generate(flipped, lift: 0f, out _).Single();
        Assert.True(Vector3.Dot(upward.Normal, downward.Normal) < -0.99f,
            "reversing the source winding must reverse the generated plane");
    }

    [Fact]
    public void ATileWithNoUsableMappingIsSkippedRatherThanGuessedAt()
    {
        // All three vertices on one UV line: no plane is recoverable, and least squares would still
        // return one.
        var degenerate = new List<DecalSourceTriangle>
        {
            new(new(0,0,0), new(10,0,0), new(20,0,0),
                new(0.1f,0.5f), new(0.2f,0.5f), new(0.3f,0.5f)),
        };
        var quads = LegacyDecalQuadGenerator.Generate(degenerate, lift: 0f, out int skipped);
        Assert.Empty(quads);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void TheGeneratorIsOffUnlessAskedFor()
    {
        // The planes discard terrain conformance, so this is a trade the user opts into.
        Assert.False(LegacyPortDecalOptions.Defaults.GenerateQuads);
        Assert.Equal(4f, LegacyPortDecalOptions.Defaults.Lift);
    }

    [Fact]
    public void TwoDistantPatchesSharingATileAveragedIntoOneIsTheCallersBugToAvoid()
    {
        // M551, pinned as a CONTRACT rather than a defect: UV tile indices are not unique across a map,
        // so two patches far apart can both sit in tile (0,0). Handed both at once the generator fits one
        // plane through their average - which is correct behaviour for "fit this geometry" and wrong for
        // "rebuild these decals". The porter must therefore scope each call to a single patch.
        var near = FlatTile(size: 100f);
        var far = FlatTile(size: 100f).Select(t => new DecalSourceTriangle(
            t.P0 + new Vector3(5000, 0, 0), t.P1 + new Vector3(5000, 0, 0), t.P2 + new Vector3(5000, 0, 0),
            t.T0, t.T1, t.T2)).ToList();

        var together = LegacyDecalQuadGenerator.Generate(near.Concat(far).ToList(), 0f, out _);
        Assert.Empty(together);   // 5,000 units of extrapolation trips the size-ratio guard

        // Scoped per patch, each lands on its own geometry.
        var a = Assert.Single(LegacyDecalQuadGenerator.Generate(near, 0f, out _));
        var b = Assert.Single(LegacyDecalQuadGenerator.Generate(far, 0f, out _));
        Assert.Equal(0f, a.A.X, 3);
        Assert.Equal(5000f, b.A.X, 3);
    }

    [Fact]
    public void ATileTheSourceBarelyEntersGetsNoPlane()
    {
        // A patch's UV runs past its own edges, clipping the corner of tiles it never really covers. A
        // full plane there floats over ground the decal does not touch. 349 such tiles on the Map2 port.
        var sliver = new List<DecalSourceTriangle>
        {
            new(new(0,0,0), new(0,0,4), new(4,0,4),
                new(0.01f,0.01f), new(0.01f,0.05f), new(0.05f,0.05f)),
        };
        Assert.Empty(LegacyDecalQuadGenerator.Generate(sliver, 0f, out int skipped));
        Assert.Equal(1, skipped);
    }

    /// <summary>
    /// M550: Avalonia bindings here are RESOLVED AT RUNTIME even with x:DataType set - a binding to a
    /// property that does not exist compiles cleanly and fails only when the window is shown. Proven by
    /// pointing one at a nonexistent name and watching the build succeed. This walks the dialog's binding
    /// paths against the view model instead.
    /// </summary>
    [Fact]
    public void EveryBindingInThePortDialogNamesARealViewModelProperty()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return;
        string axaml = Path.Combine(dir.FullName, "src", "ReyEngine.App", "Views", "LegacyMapPortWindow.axaml");
        if (!File.Exists(axaml)) return;

        // The dialog nests DataTemplates whose DataContext is a ROW view model, not the window's, so a
        // name is satisfied by any view model in the assembly. That still catches the failure that
        // matters - a binding to a name that exists nowhere, e.g. a typo or a renamed property.
        var models = typeof(ReyEngine.App.ViewModels.LegacyMapPortWindowViewModel).Assembly
            .GetTypes().Where(t => t.Namespace == "ReyEngine.App.ViewModels").ToList();
        var missing = new List<string>();
        foreach (Match m in Regex.Matches(File.ReadAllText(axaml), @"\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)"))
        {
            string name = m.Groups[1].Value;
            if (name is "Path" or "Source" or "RelativeSource" or "ElementName" or "Converter") continue;
            if (!models.Any(t => t.GetProperty(name) is not null || t.GetMethod(name) is not null))
                missing.Add(name);
        }
        Assert.True(missing.Count == 0,
            "bindings with no matching member on any view model: " + string.Join(", ", missing.Distinct()));
    }
}
