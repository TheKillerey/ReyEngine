namespace ReyEngine.Formats.MapGeo;

/// <summary>One cubemap probe whose texture will not load as a cubemap.</summary>
public sealed record CubemapProbeIssue(string ProbeName, string TexturePath, string Problem);

/// <summary>M170: checks that every texture a <c>MapCubemapProbe</c> points at really is a CUBEMAP.
///
/// This is a confirmed, shipped crash cause — and it is worth stating plainly because ReyEngine
/// previously blamed the wrong thing. The symptom was mod loaders hitting a large WAD count, so the
/// Overlay Footprint tool told users that "200+ WADs have crashed the game". That correlation was
/// wrong: wide overlays are a performance and merge-complexity concern, not a crash. The actual crash
/// was a texture authored as a plain 2D DXT1/DXT3 image being loaded where the engine expects a
/// cubemap, because a MapCubemapProbe in the map's materials .bin references it. See
/// https://github.com/LeagueToolkit/ltk-manager/issues/305
///
/// A DDS is a cubemap only when DDSCAPS2_CUBEMAP (0x200) is set in caps2 at offset 112 and the faces
/// are square. A .tex (Riot's own container) is never a cubemap, so a probe pointing at one is also
/// wrong. Both are reported.</summary>
public static class CubemapProbeValidator
{
    private const uint DdsCaps2Cubemap = 0x200;

    /// <summary>Validate every probe in a map. <paramref name="readAsset"/> resolves a texture path to
    /// its bytes and returns null when the asset is missing.</summary>
    public static List<CubemapProbeIssue> Validate(
        IEnumerable<MapCubemapProbe> probes, Func<string, byte[]?> readAsset)
    {
        var issues = new List<CubemapProbeIssue>();
        var seen = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var probe in probes)
        {
            if (probe.CubemapPath is not { Length: > 0 } path) continue;

            if (!seen.TryGetValue(path, out var cached))
            {
                cached = Diagnose(path, readAsset);
                seen[path] = cached;
            }
            if (cached is not null) issues.Add(new CubemapProbeIssue(probe.Name, path, cached));
        }
        return issues;
    }

    /// <summary>Null when the texture is a valid cubemap; otherwise why it is not.</summary>
    private static string? Diagnose(string path, Func<string, byte[]?> readAsset)
    {
        byte[]? data;
        try { data = readAsset(path); }
        catch (Exception ex) { return $"could not be read ({ex.Message})"; }
        if (data is null) return "is referenced by a probe but is missing from the project and the game";
        if (data.Length < 128) return "is too small to be a valid DDS cubemap";

        bool isDds = data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ';
        bool isTex = data[0] == 'T' && data[1] == 'E' && data[2] == 'X' && data[3] == 0;

        if (isTex)
            return "is a Riot .tex, which cannot hold a cubemap — the engine will try to bind it as one and fail";
        if (!isDds)
            return "is not a DDS file, so it cannot be bound as a cubemap";

        uint caps2 = BitConverter.ToUInt32(data, 112);
        if ((caps2 & DdsCaps2Cubemap) == 0)
        {
            string fourCc = System.Text.Encoding.ASCII.GetString(data, 84, 4).Trim('\0');
            return $"is a plain 2D {(fourCc.Length > 0 ? fourCc : "DDS")} texture — DDSCAPS2_CUBEMAP is not set, "
                 + "so the engine binds a 2D image where it expects 6 faces. THIS IS THE CRASH.";
        }

        int height = BitConverter.ToInt32(data, 12);
        int width = BitConverter.ToInt32(data, 16);
        if (width != height || width <= 0)
            return $"is flagged as a cubemap but its faces are {width}x{height} — faces must be square";

        return null;
    }
}
