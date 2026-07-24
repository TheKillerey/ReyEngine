using System.Globalization;
using System.Numerics;
using LeagueToolkit.IO.Inibin;

namespace ReyEngine.Formats.Environment;

/// <summary>
/// M149: a legacy (NVR) level's own environment lighting — the sun direction and the sun/ambient colours
/// the game lit it with. Two eras ship it two different ways:
///   * newer levels: <c>terrain.inibin</c>, whose hashed keys resolve to SUN*SunDir / SUN*SunLightColor /
///     SUN*AmbientLightColor (verified by hashing candidate names against Map1 + Map10).
///   * older levels: a plain-text <c>sun.ini</c> with SunColorN / SkyColorN / Position lines.
/// Without this the viewer lit every NVR map with a flat grey guess, so maps lost their authored mood.
/// </summary>
public sealed record NvrSunSettings
{
    /// <summary>Direction the sunlight TRAVELS (points downward), matching the renderer's uLight.</summary>
    public required Vector3 SunDirection { get; init; }
    /// <summary>Sun colour, 0..1.</summary>
    public required Vector3 SunColor { get; init; }
    /// <summary>Ambient / sky colour, 0..1.</summary>
    public required Vector3 AmbientColor { get; init; }
    /// <summary>Which file this came from — shown in the UI so the source is obvious.</summary>
    public required string Source { get; init; }

    // hashed "SECTION*Key" names (hash = c + 65599*h over the lowercased string)
    private const uint KeySunDir = 0x89ee8f7f;        // SUN*SunDir
    private const uint KeySunColor = 0x7b9e28db;      // SUN*SunLightColor
    private const uint KeyAmbient = 0xcd506187;       // SUN*AmbientLightColor

    /// <summary>Load a map folder's environment lighting; null when it ships none. Never throws.</summary>
    public static NvrSunSettings? TryLoad(string mapFolder)
    {
        return TryLoadTerrainInibin(Path.Combine(mapFolder, "terrain.inibin"))
            ?? TryLoadSunIni(Path.Combine(mapFolder, "sun.ini"));
    }

    // ---- newer levels: terrain.inibin ----
    private static NvrSunSettings? TryLoadTerrainInibin(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var ini = new InibinFile(path);
            var sun = FindVec(ini, KeySunColor);
            var amb = FindVec(ini, KeyAmbient);
            var dir = FindVec(ini, KeySunDir);
            if (sun is null && amb is null && dir is null) return null;

            return new NvrSunSettings
            {
                // Colours are authored 0..255; a couple of maps store them as a "R G B" string instead.
                SunColor = Norm(sun ?? new Vector3(255f)),
                AmbientColor = Norm(amb ?? new Vector3(60f)),
                SunDirection = Dir(dir ?? new Vector3(0.382f, -0.923f, -0.05f)),
                Source = "terrain.inibin",
            };
        }
        catch { return null; }
    }

    /// <summary>Pull a key from whichever typed set holds it — vec3 in most maps, a "R G B" string in some.</summary>
    private static Vector3? FindVec(InibinFile ini, uint key)
    {
        foreach (var set in ini.Sets.Values)
        {
            if (set.Properties is null || !set.Properties.TryGetValue(key, out object? v) || v is null) continue;
            switch (v)
            {
                case float[] f when f.Length >= 3: return new Vector3(f[0], f[1], f[2]);
                case string s when ParseTriple(s) is { } t: return t;
                case float f1: return new Vector3(f1);
            }
        }
        return null;
    }

    private static Vector3? ParseTriple(string s)
    {
        var parts = s.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            return new Vector3(x, y, z);
        return null;
    }

    // ---- older levels: sun.ini (plain text) ----
    private static NvrSunSettings? TryLoadSunIni(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            Vector3? sun = null, sky = null;
            float azimuth = 255f, elevation = 35f;
            bool sawPosition = false;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
                // SunColor1 is the primary sun tint; the higher-numbered ones are the dusk ramp.
                if (sun is null && p[0].Equals("SunColor1", StringComparison.OrdinalIgnoreCase))
                    sun = ParseTriple(string.Join(' ', p.Skip(1)));
                else if (sky is null && p[0].StartsWith("SkyColor", StringComparison.OrdinalIgnoreCase))
                    sky = ParseTriple(string.Join(' ', p.Skip(1)));
                else if (p.Length >= 3 && p[0].Equals("Position", StringComparison.OrdinalIgnoreCase)
                         && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float a)
                         && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float e))
                { azimuth = a; elevation = e; sawPosition = true; }
            }
            if (sun is null && sky is null) return null;

            // Position is (azimuth, elevation) in degrees; build the downward travel direction from it.
            // Elevation is clamped away from the horizon so a grazing value can't flatten all shading.
            float el = Math.Clamp(sawPosition ? elevation : 35f, 10f, 80f) * MathF.PI / 180f;
            float az = azimuth * MathF.PI / 180f;
            float horiz = MathF.Cos(el);
            var dir = new Vector3(-horiz * MathF.Sin(az), -MathF.Sin(el), -horiz * MathF.Cos(az));

            return new NvrSunSettings
            {
                SunColor = Norm(sun ?? new Vector3(255f)),
                AmbientColor = Norm(sky ?? new Vector3(70f)),
                SunDirection = Dir(dir),
                Source = "sun.ini",
            };
        }
        catch { return null; }
    }

    /// <summary>Authored 0..255 (or already 0..1 in a few maps) → 0..1.</summary>
    private static Vector3 Norm(Vector3 c)
    {
        float m = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        var v = m > 1.001f ? c / 255f : c;
        return Vector3.Clamp(v, Vector3.Zero, Vector3.One);
    }

    private static Vector3 Dir(Vector3 d)
    {
        float len = d.Length();
        if (len < 1e-4f) return new Vector3(0.382f, -0.923f, -0.05f);
        d /= len;
        // The renderer expects the direction light travels (downward); flip an upward-pointing vector.
        return d.Y > 0f ? -d : d;
    }
}
