using System.Globalization;
using System.Numerics;

namespace ReyEngine.Formats.Lighting;

/// <summary>One legacy Riot point light (Light.dat): world-space position, linear RGB colour (0..1), radius.</summary>
public readonly record struct PointLight(Vector3 Position, Vector3 Color, float Radius);

/// <summary>
/// Reads Riot's old <c>LEVELS/MapN/Light.dat</c> point-light table — how the pre-2013 client placed the
/// torch/brazier point lights. Plain ASCII text, one light per line, seven whitespace-separated numbers:
/// <c>X Y Z R G B Radius</c> — position and radius in League world units (Y up), colour 0..255. Never
/// throws; malformed / short lines are skipped so a partially-corrupt file still yields the lights it can.
/// </summary>
public static class LightDatFile
{
    public static IReadOnlyList<PointLight> Parse(byte[] data) =>
        ParseText(System.Text.Encoding.ASCII.GetString(data));

    public static IReadOnlyList<PointLight> ParseText(string text)
    {
        var lights = new List<PointLight>();
        foreach (var rawLine in text.Split('\n'))
        {
            var tok = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length < 7) continue;
            if (!(TryF(tok[0], out var x) && TryF(tok[1], out var y) && TryF(tok[2], out var z)
               && TryF(tok[3], out var r) && TryF(tok[4], out var g) && TryF(tok[5], out var b)
               && TryF(tok[6], out var radius))) continue;
            if (radius <= 0f) continue;                    // a zero-radius light contributes nothing
            lights.Add(new PointLight(new Vector3(x, y, z), new Vector3(r, g, b) / 255f, radius));
        }
        return lights;
    }

    private static bool TryF(string s, out float v) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
