using System.Globalization;
using System.Numerics;

namespace ReyEngine.Formats.Lighting;

/// <summary>One legacy Riot point light (Light.dat): world-space position, linear RGB colour (0..1), radius.
/// M153: <paramref name="Intensity"/> is a PER-LIGHT strength multiplier on top of the colour. Light.dat has
/// no such field (a line is only X Y Z R G B Radius), so it is an editor-side value — the writer folds it
/// into the colour, which is exactly how the format expresses a brighter light.</summary>
public readonly record struct PointLight(Vector3 Position, Vector3 Color, float Radius, float Intensity = 1f);

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

    /// <summary>M152: write the table back in Riot's own format — one light per line,
    /// <c>X Y Z R G B Radius</c>, colour scaled back to 0..255 and everything invariant-formatted so a
    /// German locale can't emit commas the game (or our own reader) would reject.</summary>
    public static string ToText(IEnumerable<PointLight> lights)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var l in lights)
        {
            if (l.Radius <= 0f) continue;   // Parse drops these, so never write one
            // M153: fold the per-light strength into the colour — the only way the format can carry it.
            var c = Vector3.Clamp(l.Color * MathF.Max(l.Intensity, 0f), Vector3.Zero, Vector3.One) * 255f;
            sb.Append(F(l.Position.X)).Append(' ').Append(F(l.Position.Y)).Append(' ').Append(F(l.Position.Z)).Append(' ')
              .Append(F(MathF.Round(c.X))).Append(' ').Append(F(MathF.Round(c.Y))).Append(' ').Append(F(MathF.Round(c.Z))).Append(' ')
              .Append(F(l.Radius)).Append('\n');
        }
        return sb.ToString();
    }

    public static byte[] Write(IEnumerable<PointLight> lights) =>
        System.Text.Encoding.ASCII.GetBytes(ToText(lights));

    private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
