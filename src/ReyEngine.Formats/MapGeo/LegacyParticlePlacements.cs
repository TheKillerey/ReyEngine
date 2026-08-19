using System.Globalization;
using System.Numerics;

namespace ReyEngine.Formats.MapGeo;

/// <summary>One line of a legacy <c>Particles.dat</c>.</summary>
/// <param name="SystemPath">The reference exactly as written, e.g. <c>Data\Particles\CANDLE.troy</c>.
/// Separators and case vary line to line even within one file.</param>
/// <param name="Position">World position. Measured to be the SAME space as the NVR vertices, so a port
/// applies whatever translation it gave the geometry and nothing else.</param>
/// <param name="DetailLevel">Token 5. A graphics-detail gate, not an id: the client skips the placement
/// when this exceeds the environment-quality setting. <see cref="AlwaysSpawns"/> reads it.</param>
/// <param name="RawVector">Tokens 6-8 exactly as written. See <see cref="ClientVector"/> - the client
/// does not use all three - and note the MEANING of these is unresolved.</param>
/// <param name="EmitterGroup">Token 9, when present. A group name the client's script API can pause and
/// restart as a unit. Mutually exclusive with <see cref="RawVector"/> being applied.</param>
/// <param name="IgnoredTokens">Anything past token 9. The client's format has only 9 conversions, so
/// these are read by nothing - recorded rather than dropped so a port can report them.</param>
/// <param name="TokenCount">How many tokens the line actually had. This is not cosmetic: it is what
/// decides whether the client applies the vector or the group.</param>
/// <param name="LineNumber">1-based, for diagnostics.</param>
public sealed record LegacyParticlePlacement(
    string SystemPath,
    Vector3 Position,
    int DetailLevel,
    Vector3 RawVector,
    string? EmitterGroup,
    IReadOnlyList<string> IgnoredTokens,
    int TokenCount,
    int LineNumber)
{
    /// <summary>The file stem, lower-cased - the key a <c>.troybin</c> is resolved by.</summary>
    public string SystemName
    {
        get
        {
            string s = SystemPath.Replace('\\', '/');
            int slash = s.LastIndexOf('/');
            if (slash >= 0) s = s[(slash + 1)..];
            int dot = s.LastIndexOf('.');
            return (dot >= 0 ? s[..dot] : s).ToLowerInvariant();
        }
    }

    /// <summary>
    /// The vector the CLIENT actually builds from tokens 6-8: <c>(f6, f7, f7)</c>. The eighth token is
    /// parsed and then never read - that is why f7 and f8 are equal on 2,302 of 2,306 corpus lines.
    ///
    /// <para>What this vector MEANS is unresolved. It is passed to an emitter vtable slot whose class has
    /// its RTTI stripped, and the loader is the only one of 42 call sites that touches that slot, so there
    /// is no second use to read the semantics from. Euler degrees is a plausible reading and is NOT
    /// established. Nothing here applies it.</para>
    /// </summary>
    public Vector3 ClientVector => new(RawVector.X, RawVector.Y, RawVector.Y);

    /// <summary>Whether the client's detail gate lets this placement spawn at every graphics setting.
    /// <c>int.MinValue</c> is the value the loader pre-sets before parsing, so it means "unspecified".</summary>
    public bool AlwaysSpawns => DetailLevel == int.MinValue;

    /// <summary>
    /// Whether the client applies <see cref="ClientVector"/> to this placement. Only when the line had
    /// exactly 8 tokens - a line carrying a group name gets the group instead, never both. The two are
    /// separate branches on the parse count in the loader.
    /// </summary>
    public bool VectorIsApplied => TokenCount == 8;
}

/// <summary>What a parse of one <c>Particles.dat</c> produced.</summary>
public sealed record LegacyParticlePlacementSet(
    IReadOnlyList<LegacyParticlePlacement> Placements,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Distinct system stems, lower-cased, in first-seen order.</summary>
    public IReadOnlyList<string> SystemNames =>
        Placements.Select(p => p.SystemName).Distinct(StringComparer.Ordinal).ToArray();

    public string Summary =>
        $"{Placements.Count} placement(s) referencing {SystemNames.Count} particle system(s)"
        + (Warnings.Count == 0 ? "." : $"; {Warnings.Count} line(s) skipped.");
}

/// <summary>
/// Reads the legacy <c>Particles.dat</c> placement list (M531).
///
/// <para>The format is not inferred from the data - it is the client's own, recovered from
/// <c>League of Legends.exe</c>. The loader at VA 0x6C86D0 reads each line and calls sscanf with the
/// literal format at VA 0x10A5354:</para>
/// <code>%s %g %g %g %d %g %g %g %s</code>
/// <para>giving <c>path x y z detailLevel f6 f7 f8 [group]</c>. Three consequences are worth stating
/// because none of them is visible in the data alone:</para>
/// <list type="bullet">
/// <item>Token 5 is pre-set to 0x80000000 before the call, so <c>-2147483648</c> means "not written".
/// It gates on graphics detail (VA 0x6C8990), it is not an id or an attachment handle.</item>
/// <item>Token 8 is parsed and discarded - the client composes <c>(f6, f7, f7)</c>.</item>
/// <item>The vector and the group are MUTUALLY EXCLUSIVE branches on the token count, so a line can
/// carry one or the other and never both.</item>
/// </list>
///
/// <para>There are only 9 conversions, so a 10th token (map11's <c>CHAOSONLY</c>/<c>ORDERONLY</c>) is
/// read by nothing in the client. Those strings do not appear in the executable at all. They are kept
/// in <see cref="LegacyParticlePlacement.IgnoredTokens"/> so a port can say so rather than imply the
/// filter works.</para>
///
/// <para>Blank lines are SKIPPED here. The client does not skip them - it leaves the previous line's
/// path in the buffer and spawns a duplicate - but that is a bug to avoid reproducing, not behaviour to
/// match, and no shipped file relies on it.</para>
/// </summary>
public static class LegacyParticlePlacements
{
    /// <summary>The value the loader pre-sets token 5 to, meaning the writer left it unspecified.</summary>
    public const int UnspecifiedDetailLevel = int.MinValue;

    public static LegacyParticlePlacementSet Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var placements = new List<LegacyParticlePlacement>();
        var warnings = new List<string>();
        var lines = text.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;

            int number = i + 1;
            string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // sscanf needs the first four conversions to land or there is no placement at all. Anything
            // shorter is a malformed line, not a shorter dialect.
            if (t.Length < 4)
            {
                warnings.Add($"line {number}: only {t.Length} token(s) - skipped");
                continue;
            }

            if (!F(t[1], out float x) || !F(t[2], out float y) || !F(t[3], out float z))
            {
                warnings.Add($"line {number}: position is not three numbers - skipped");
                continue;
            }

            // Token 5 onward are optional in the sense that sscanf stops where the line stops; a short
            // line simply leaves the later slots at their pre-set values.
            int detail = t.Length > 4 && int.TryParse(t[4], NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int d) ? d : UnspecifiedDetailLevel;

            F(t.Length > 5 ? t[5] : "0", out float f6);
            F(t.Length > 6 ? t[6] : "0", out float f7);
            F(t.Length > 7 ? t[7] : "0", out float f8);

            string? group = t.Length > 8 ? t[8] : null;
            string[] ignored = t.Length > 9 ? t[9..] : Array.Empty<string>();

            placements.Add(new LegacyParticlePlacement(
                t[0], new Vector3(x, y, z), detail, new Vector3(f6, f7, f8),
                group, ignored, t.Length, number));
        }

        return new LegacyParticlePlacementSet(placements, warnings);
    }

    public static LegacyParticlePlacementSet ParseFile(string path) =>
        Parse(File.ReadAllText(path));

    /// <summary>German locale ships a comma decimal separator, so every float here is invariant.</summary>
    private static bool F(string s, out float value) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
