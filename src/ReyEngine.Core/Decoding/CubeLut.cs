using System.Globalization;
using System.Numerics;

namespace ReyEngine.Core.Decoding;

/// <summary>M173: an Adobe/IRIDAS <c>.cube</c> colour lookup table.
///
/// The format is plain text: optional TITLE, a DOMAIN_MIN/DOMAIN_MAX pair, either LUT_1D_SIZE or
/// LUT_3D_SIZE, then that many RGB triples. A 3D table is stored RED-FASTEST — red varies over
/// consecutive lines, then green, then blue — which is the opposite of the row-major order most people
/// assume and the usual reason a hand-rolled reader comes out with the channels transposed.
///
/// Sampled with trilinear interpolation. A 32^3 grade (the size Photoshop exports, and the size of the
/// test file this was written against) has 32,768 entries and quantises visibly at hard gradients if you
/// sample it nearest-neighbour.</summary>
public sealed class CubeLut
{
    /// <summary>Grid resolution per axis. A 1D LUT is stored as a degenerate case with <see cref="Is1D"/>.</summary>
    public int Size { get; }
    public bool Is1D { get; }
    public string Title { get; }
    public Vector3 DomainMin { get; }
    public Vector3 DomainMax { get; }

    /// <summary>Size^3 entries for a 3D LUT (red fastest), or Size entries for a 1D one.</summary>
    private readonly Vector3[] _data;

    private CubeLut(int size, bool is1D, string title, Vector3 domainMin, Vector3 domainMax, Vector3[] data)
    {
        Size = size; Is1D = is1D; Title = title;
        DomainMin = domainMin; DomainMax = domainMax; _data = data;
    }

    public static CubeLut Load(string path) => Parse(File.ReadAllLines(path), Path.GetFileNameWithoutExtension(path));

    /// <summary>Parse .cube text. Throws <see cref="InvalidDataException"/> with a specific reason rather
    /// than returning a half-built table — a silently wrong grade is worse than a failed load.</summary>
    public static CubeLut Parse(IEnumerable<string> lines, string fallbackTitle = "")
    {
        int size = 0;
        bool is1D = false;
        string title = fallbackTitle;
        var domainMin = Vector3.Zero;
        var domainMax = Vector3.One;
        List<Vector3>? data = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            // Keywords are case-insensitive in practice; real files in the wild vary.
            if (line.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase))
            {
                int q1 = line.IndexOf('"'), q2 = line.LastIndexOf('"');
                title = q2 > q1 && q1 >= 0 ? line[(q1 + 1)..q2] : line[5..].Trim();
                continue;
            }
            if (TryKeyword(line, "LUT_3D_SIZE", out var s3)) { size = (int)s3.X; is1D = false; data = new List<Vector3>(size * size * size); continue; }
            if (TryKeyword(line, "LUT_1D_SIZE", out var s1)) { size = (int)s1.X; is1D = true; data = new List<Vector3>(size); continue; }
            if (TryKeyword(line, "DOMAIN_MIN", out var dmin)) { domainMin = dmin; continue; }
            if (TryKeyword(line, "DOMAIN_MAX", out var dmax)) { domainMax = dmax; continue; }
            if (line.StartsWith("LUT_", StringComparison.OrdinalIgnoreCase)) continue;   // unknown directive

            if (!TryTriple(line, out var rgb)) continue;
            data ??= new List<Vector3>();
            data.Add(rgb);
        }

        if (size <= 1) throw new InvalidDataException("no LUT_3D_SIZE or LUT_1D_SIZE found (is this a .cube file?)");
        if (data is null) throw new InvalidDataException("the file declares a size but contains no data points");

        int expected = is1D ? size : size * size * size;
        if (data.Count != expected)
            throw new InvalidDataException($"expected {expected:n0} data points for {(is1D ? "1D" : "3D")} size {size}, found {data.Count:n0}");

        return new CubeLut(size, is1D, title, domainMin, domainMax, data.ToArray());
    }

    private static bool TryKeyword(string line, string keyword, out Vector3 value)
    {
        value = default;
        if (!line.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = line[keyword.Length..].Trim();
        var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        // Floats are always '.'-decimal in .cube regardless of the machine's locale, so parse invariant.
        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float a)) return false;
        float b = a, c = a;
        if (parts.Length >= 3
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float pb)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float pc))
        { b = pb; c = pc; }
        value = new Vector3(a, b, c);
        return true;
    }

    private static bool TryTriple(string line, out Vector3 rgb)
    {
        rgb = default;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)) return false;
        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g)) return false;
        if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b)) return false;
        rgb = new Vector3(r, g, b);
        return true;
    }

    /// <summary>Look up a colour, trilinearly interpolated. Input outside the domain is clamped.</summary>
    public Vector3 Sample(Vector3 rgb)
    {
        var span = DomainMax - DomainMin;
        float nx = span.X > 1e-9f ? (rgb.X - DomainMin.X) / span.X : 0f;
        float ny = span.Y > 1e-9f ? (rgb.Y - DomainMin.Y) / span.Y : 0f;
        float nz = span.Z > 1e-9f ? (rgb.Z - DomainMin.Z) / span.Z : 0f;
        nx = Math.Clamp(nx, 0f, 1f); ny = Math.Clamp(ny, 0f, 1f); nz = Math.Clamp(nz, 0f, 1f);

        int last = Size - 1;
        if (Is1D)
        {
            // Each channel indexes the same curve independently.
            return new Vector3(Lerp1(nx).X, Lerp1(ny).Y, Lerp1(nz).Z);
        }

        float fx = nx * last, fy = ny * last, fz = nz * last;
        int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz;
        int x1 = Math.Min(x0 + 1, last), y1 = Math.Min(y0 + 1, last), z1 = Math.Min(z0 + 1, last);
        float tx = fx - x0, ty = fy - y0, tz = fz - z0;

        var c000 = At(x0, y0, z0); var c100 = At(x1, y0, z0);
        var c010 = At(x0, y1, z0); var c110 = At(x1, y1, z0);
        var c001 = At(x0, y0, z1); var c101 = At(x1, y0, z1);
        var c011 = At(x0, y1, z1); var c111 = At(x1, y1, z1);

        var c00 = Vector3.Lerp(c000, c100, tx);
        var c10 = Vector3.Lerp(c010, c110, tx);
        var c01 = Vector3.Lerp(c001, c101, tx);
        var c11 = Vector3.Lerp(c011, c111, tx);
        return Vector3.Lerp(Vector3.Lerp(c00, c10, ty), Vector3.Lerp(c01, c11, ty), tz);

        Vector3 Lerp1(float n)
        {
            float f = n * last;
            int i0 = (int)f, i1 = Math.Min(i0 + 1, last);
            return Vector3.Lerp(_data[i0], _data[i1], f - i0);
        }
    }

    /// <summary>RED IS THE FASTEST-VARYING AXIS in a .cube 3D table. Getting this backwards produces a
    /// grade whose red and blue responses are swapped — plausible-looking output, completely wrong.</summary>
    private Vector3 At(int r, int g, int b) => _data[(b * Size + g) * Size + r];

    /// <summary>Bake to a 256-entry-per-channel table? No — a 3D LUT is not separable, so there is no
    /// correct 1D reduction. This exists to answer "is this table doing anything at all", which is worth
    /// knowing before spending an encode on it: an identity grade should be skipped.</summary>
    public bool IsIdentity(float tolerance = 1f / 512f)
    {
        int last = Size - 1;
        if (last <= 0) return true;
        for (int b = 0; b <= last; b++)
            for (int g = 0; g <= last; g++)
                for (int r = 0; r <= last; r++)
                {
                    var want = new Vector3(r / (float)last, g / (float)last, b / (float)last);
                    var got = At(r, g, b);
                    if (MathF.Abs(got.X - want.X) > tolerance
                        || MathF.Abs(got.Y - want.Y) > tolerance
                        || MathF.Abs(got.Z - want.Z) > tolerance) return false;
                }
        return true;
    }
}
