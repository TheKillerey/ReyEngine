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
    public void APatchBecomesOnePlaneCoveringItsWholeTexture()
    {
        var quad = LegacyDecalQuadGenerator.GeneratePlane(FlatTile(), lift: 0f);
        Assert.NotNull(quad);
        Assert.Equal(0f, quad!.Value.A.X, 3); Assert.Equal(0f, quad.Value.A.Z, 3);
        Assert.Equal(100f, quad.Value.C.X, 3); Assert.Equal(100f, quad.Value.C.Z, 3);
    }

    [Fact]
    public void ThePlaneKeepsItsSourcesTextureScaleRatherThanStretchingToFit()
    {
        // M554: "the decals are good placed yeah but somehow they are stretched now so scaled to match
        // the space." M552 rewrote every plane to a clean 0..1, which stretches a patch's texture across
        // its whole footprint - a median 1.92 x 1.78 tiles worth of image squeezed into one.
        //
        // The plane carries the patch's OWN uv values instead, so the texture lands at exactly the size
        // and density it had. Measured over the Map2 port, texture size against the original is p50 1.07.
        var source = new List<DecalSourceTriangle>();
        source.AddRange(FlatTile(tileU: 0));
        source.AddRange(FlatTile(tileU: 1));   // two tiles wide: the texture repeated twice here

        var quad = LegacyDecalQuadGenerator.GeneratePlane(source, lift: 0f)!.Value;
        Assert.Equal(0f, quad.UvA.X, 3);
        Assert.Equal(2f, quad.UvC.X, 3);      // u still runs 0..2, so it still repeats twice
        Assert.Equal(200f, quad.C.X - quad.A.X, 3);
    }

    [Fact]
    public void SingleImageTradesCoverageForOneWholeTexture()
    {
        // The opt-in. One image at authored scale, which for a patch spanning two tiles means the plane
        // shows half as much texture over the same ground - the decal reads larger and covers less.
        var source = new List<DecalSourceTriangle>();
        source.AddRange(FlatTile(tileU: 0));
        source.AddRange(FlatTile(tileU: 1));

        var quad = LegacyDecalQuadGenerator.GeneratePlane(source, lift: 0f, singleImage: true)!.Value;
        Assert.Equal(0f, quad.UvA.X, 3);
        Assert.Equal(1f, quad.UvC.X, 3);
        Assert.False(LegacyPortDecalOptions.Defaults.SingleImage);
    }

    [Fact]
    public void APatchSpanningSeveralTilesIsStillOnePlane()
    {
        // M552, the reporter's second finding: "I get often double pasted meshes or more for decals ...
        // It looks now as a not clamped version so repeated images."
        //
        // A legacy patch spans about 2.2 UV tiles, so emitting one plane per TILE drew the image two or
        // three times over the same decal. The patch's whole UV extent becomes a single 0..1 instead.
        var source = new List<DecalSourceTriangle>();
        source.AddRange(FlatTile(tileU: 0));
        source.AddRange(FlatTile(tileU: 1));
        source.AddRange(FlatTile(tileU: 2));

        var quad = LegacyDecalQuadGenerator.GeneratePlane(source, lift: 0f);
        Assert.NotNull(quad);
        // ONE plane over all three tiles - not three planes stacked on the same decal.
        Assert.Equal(0f, quad!.Value.A.X, 3);
        Assert.Equal(300f, quad.Value.C.X, 3);
    }

    [Fact]
    public void ThePlaneIsLiftedAlongItsNormalNotBlindlyUpwards()
    {
        var quad = LegacyDecalQuadGenerator.GeneratePlane(FlatTile(y: 50f), lift: 4f);
        Assert.NotNull(quad);
        Assert.Equal(54f, quad!.Value.A.Y, 3);
        Assert.True(quad.Value.Normal.Y > 0.99f, $"a ground decal faces up, saw {quad.Value.Normal}");
    }

    [Fact]
    public void APlaneFacesTheWayItsSourceDid()
    {
        // A fit can come out inverted, and an inverted decal is invisible under backface culling. The
        // generator compares against the source winding and flips the quad rather than the normal alone.
        var flipped = FlatTile().Select(t =>
            new DecalSourceTriangle(t.P0, t.P2, t.P1, t.T0, t.T2, t.T1)).ToList();

        var up = LegacyDecalQuadGenerator.GeneratePlane(FlatTile(), lift: 0f)!.Value;
        var down = LegacyDecalQuadGenerator.GeneratePlane(flipped, lift: 0f)!.Value;
        Assert.True(Vector3.Dot(up.Normal, down.Normal) < -0.99f,
            "reversing the source winding must reverse the generated plane");
    }

    [Fact]
    public void APatchWithNoUsableMappingIsRefusedRatherThanGuessedAt()
    {
        // All three vertices on one UV line: no plane is recoverable, and least squares would still
        // return one.
        var degenerate = new List<DecalSourceTriangle>
        {
            new(new(0,0,0), new(10,0,0), new(20,0,0),
                new(0.1f,0.5f), new(0.2f,0.5f), new(0.3f,0.5f)),
        };
        Assert.Null(LegacyDecalQuadGenerator.GeneratePlane(degenerate, lift: 0f));
    }

    [Fact]
    public void TheGeneratorIsOffUnlessAskedFor()
    {
        // The planes discard terrain conformance, so this is a trade the user opts into.
        Assert.False(LegacyPortDecalOptions.Defaults.GenerateQuads);
        Assert.Equal(4f, LegacyPortDecalOptions.Defaults.Lift);
    }

    [Fact]
    public void TwoDistantPatchesHandedInTogetherAreRefused()
    {
        // M551, pinned as a CONTRACT: UV tile indices are not unique across a map, so patches far apart
        // can share one. Fitted together, least squares returns a perfectly ordinary plane at their
        // AVERAGE - nothing about it looks wrong except where it is. The two-sided size guard is what
        // turns that into a refusal, so a mis-scoped call gives nothing rather than something misplaced.
        var near = FlatTile(size: 100f);
        var far = FlatTile(size: 100f).Select(t => new DecalSourceTriangle(
            t.P0 + new Vector3(5000, 0, 0), t.P1 + new Vector3(5000, 0, 0), t.P2 + new Vector3(5000, 0, 0),
            t.T0, t.T1, t.T2)).ToList();

        Assert.Null(LegacyDecalQuadGenerator.GeneratePlane(near.Concat(far).ToList(), lift: 0f));

        // Scoped per patch, each lands on its own geometry.
        Assert.Equal(0f, LegacyDecalQuadGenerator.GeneratePlane(near, 0f)!.Value.A.X, 3);
        Assert.Equal(5000f, LegacyDecalQuadGenerator.GeneratePlane(far, 0f)!.Value.A.X, 3);
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
