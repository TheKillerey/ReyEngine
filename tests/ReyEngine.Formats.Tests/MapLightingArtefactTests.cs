using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M515: telling the capture bug's leftovers apart from a sun someone chose.
///
/// <para>A map load published the map's point lights before its MapSunProperties had been read, and the
/// publish captured — so the per-map record was written with the sliders still at the renderer's no-sun
/// fallback. Every later open restored that fallback over the map's authored sun, which is why the sun
/// "never applied on open" and came back the moment Reset to map was pressed.</para>
///
/// <para>Recognising the artefact has to be tight: a record that merely resembles it must be honoured,
/// because throwing away a real edit is the worse failure of the two.</para>
/// </summary>
public sealed class MapLightingArtefactTests
{
    /// <summary>The exact record the bug produced, read out of a real project.json.</summary>
    private static MapLightingRecord Artefact() => new()
    {
        PathHash = 15660103517351581210,
        MapgeoPath = "data/maps/mapgeometry/map453/jade_container.mapgeo",
        SunIntensity = 1, SunColorR = 0.75, SunColorG = 0.75, SunColorB = 0.75,
        SkyIntensity = 1, SkyColorR = 0.35, SkyColorG = 0.35, SkyColorB = 0.35,
        SunDirX = 0.4, SunDirY = 0.85, SunDirZ = 0.45,
        FogStartRaw = 0, FogEndRaw = 0,
    };

    [Fact]
    public void TheRecordTheBugWroteIsRecognised()
    {
        Assert.True(MapLightingArtefact.LooksLikeUntouchedFallback(Artefact()));
    }

    [Theory]
    // One field moved is enough to make it someone's choice, whichever field it is.
    [InlineData("sun intensity")]
    [InlineData("sun colour")]
    [InlineData("sky intensity")]
    [InlineData("sky colour")]
    [InlineData("direction")]
    [InlineData("fog")]
    public void AnythingActuallyEditedIsHonoured(string what)
    {
        var rec = Artefact();
        switch (what)
        {
            case "sun intensity": rec.SunIntensity = 0.5; break;
            case "sun colour": rec.SunColorR = 0.87; break;
            case "sky intensity": rec.SkyIntensity = 1.4; break;
            case "sky colour": rec.SkyColorB = 0.2; break;
            case "direction": rec.SunDirY = 0.6; break;
            case "fog": rec.FogEndRaw = 12000; break;
        }
        Assert.False(MapLightingArtefact.LooksLikeUntouchedFallback(rec));
    }

    [Fact]
    public void ARecordFromBeforeTheseFieldsExistedStillCounts()
    {
        // M463 added the direction and fog fields as nullable, and null means "predates the field" — not
        // "the user set it to zero". A pre-M463 artefact is still an artefact.
        var rec = Artefact();
        rec.SunDirX = null; rec.SunDirY = null; rec.SunDirZ = null;
        rec.FogStartRaw = null; rec.FogEndRaw = null;
        Assert.True(MapLightingArtefact.LooksLikeUntouchedFallback(rec));
    }

    [Fact]
    public void ASunAlmostButNotQuiteTheDefaultIsNotAnArtefact()
    {
        // The match is tight (1e-9) precisely so a value someone dialled in near the default survives.
        // 0.7500001 is a choice, not the fallback.
        var rec = Artefact();
        rec.SunColorG = 0.7500001;
        Assert.False(MapLightingArtefact.LooksLikeUntouchedFallback(rec));
    }

    [Fact]
    public void TheLightsInAnArtefactRecordAreStillReal()
    {
        // The user placed those. Only the sun block came from the bug, which is why the two halves are
        // restored separately.
        var rec = Artefact();
        rec.Lights.Add(new SavedPointLight { X = 1083, Y = 426, Z = 882, R = 116, G = 94, B = 80, Radius = 320 });
        Assert.True(MapLightingArtefact.LooksLikeUntouchedFallback(rec));
        Assert.Single(rec.Lights);
    }
}
