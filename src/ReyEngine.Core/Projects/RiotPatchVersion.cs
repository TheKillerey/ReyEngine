using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReyEngine.Core.Projects;

/// <summary>The installed Riot build reduced to the CommunityDragon patch name used for rebasing.</summary>
public sealed record RiotPatchVersion(string Patch, string BuildVersion, string Source);

/// <summary>
/// Detects the patch represented by a League <c>Game</c> directory. Riot's content metadata is the
/// authoritative source; the executable version is a fallback for older or incomplete installs.
/// </summary>
public static class RiotPatchVersionDetector
{
    private static readonly Regex NumericVersion = new(@"(?<!\d)(?<major>\d{2})\.(?<minor>\d{1,2})(?:\.|$)", RegexOptions.Compiled);
    private static readonly Regex BranchVersion = new(@"releases-(?<major>\d{2})-(?<minor>\d{1,2})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static RiotPatchVersion? Detect(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory)) return null;

        foreach (string name in new[] { "content-metadata.json", "code-metadata.json", "compat-version-metadata.json" })
        {
            string file = Path.Combine(gameDirectory, name);
            try
            {
                if (!File.Exists(file)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("version", out var value)) continue;
                string build = value.GetString() ?? "";
                if (TryNormalize(build, out var patch)) return new RiotPatchVersion(patch, build, name);
            }
            catch { /* try the next authoritative source */ }
        }

        try
        {
            string exe = Path.Combine(gameDirectory, "League of Legends.exe");
            if (File.Exists(exe))
            {
                string build = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "";
                if (TryNormalize(build, out var patch)) return new RiotPatchVersion(patch, build, Path.GetFileName(exe));
            }
        }
        catch { /* best effort */ }
        return null;
    }

    /// <summary>Normalize <c>16.15.8013452</c>, <c>releases-16-15</c>, or <c>16.15</c> to <c>16.15</c>.</summary>
    public static bool TryNormalize(string? value, out string patch)
    {
        patch = "";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = NumericVersion.Match(value);
        if (!match.Success) match = BranchVersion.Match(value);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups["major"].Value, out int major)
            || !int.TryParse(match.Groups["minor"].Value, out int minor)
            || major < 10 || minor < 0 || minor > 99)
            return false;
        patch = $"{major}.{minor}";
        return true;
    }

    /// <summary>
    /// Patch-style mod versions (<c>16.10.0</c>) are a useful migration hint for projects created before
    /// patch tracking existed. Ordinary semantic versions (<c>1.0.0</c>) deliberately return null.
    /// </summary>
    public static string? InferProjectBaseline(string? modVersion, string currentPatch)
    {
        if (!TryNormalize(modVersion, out var inferred)) return null;
        return Compare(inferred, currentPatch) <= 0 ? inferred : null;
    }

    public static int Compare(string left, string right)
    {
        static (int Major, int Minor) Parts(string value)
        {
            if (!TryNormalize(value, out var normalized)) return (-1, -1);
            int dot = normalized.IndexOf('.');
            return (int.Parse(normalized[..dot]), int.Parse(normalized[(dot + 1)..]));
        }
        return Parts(left).CompareTo(Parts(right));
    }
}
