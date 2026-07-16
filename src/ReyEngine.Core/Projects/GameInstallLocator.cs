using System.Text.RegularExpressions;

namespace ReyEngine.Core.Projects;

/// <summary>A discovered League of Legends installation (M73). Platform is "Live" or "PBE".</summary>
public sealed record GameInstall(string Platform, string GameDirectory)
{
    public string Display => $"{Platform} — {GameDirectory}";
}

/// <summary>One game WAD available for a new project (M73), grouped for the wizard's picker.</summary>
public sealed record GameWad(string Path, string Group, string Name, long SizeBytes)
{
    public string SizeDisplay => SizeBytes >= 1 << 30
        ? $"{SizeBytes / (double)(1 << 30):0.0} GB"
        : $"{SizeBytes / (double)(1 << 20):0.0} MB";
}

/// <summary>
/// M73: discovers League installations (Live + PBE) and lists the WADs inside one, grouped the way the
/// New Project wizard presents them (Champions / Maps / Core &amp; UI / Voice-locale). Best effort, never throws.
/// </summary>
public static class GameInstallLocator
{
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    /// <summary>Find installed League clients: Riot metadata first (authoritative), then common paths.</summary>
    public static List<GameInstall> Discover()
    {
        var found = new List<GameInstall>();
        void Add(string platform, string gameDir)
        {
            if (!Directory.Exists(gameDir)) return;
            if (found.Any(i => string.Equals(i.GameDirectory, gameDir, OIC))) return;
            found.Add(new GameInstall(platform, gameDir));
        }

        // Riot client metadata: C:\ProgramData\Riot Games\Metadata\league_of_legends.<line>\*.product_settings.yaml
        try
        {
            var meta = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "Metadata");
            if (Directory.Exists(meta))
                foreach (var dir in Directory.EnumerateDirectories(meta, "league_of_legends*"))
                {
                    string platform = System.IO.Path.GetFileName(dir).Contains("pbe", OIC) ? "PBE" : "Live";
                    foreach (var yaml in Directory.EnumerateFiles(dir, "*.product_settings.yaml"))
                    {
                        var m = Regex.Match(File.ReadAllText(yaml), @"product_install_full_path:\s*""?([^""\r\n]+)""?");
                        if (m.Success)
                            Add(platform, System.IO.Path.Combine(m.Groups[1].Value.Trim().Replace('/', '\\'), "Game"));
                    }
                }
        }
        catch { /* metadata unreadable — fall through to common paths */ }

        foreach (var drive in new[] { "C:", "D:", "E:" })
        {
            Add("Live", $@"{drive}\Riot Games\League of Legends\Game");
            Add("PBE", $@"{drive}\Riot Games\League of Legends (PBE)\Game");
        }
        Add("Live", @"C:\Program Files\Riot Games\League of Legends\Game");
        return found;
    }

    /// <summary>List a game install's WADs grouped for the wizard. Locale wads (VO audio) are their own group.</summary>
    public static List<GameWad> ListWads(string gameDirectory)
    {
        var result = new List<GameWad>();
        var final = FindFinalDir(gameDirectory);
        if (final is null) return result;

        void AddDir(string dir, string group, bool splitLocale)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*.wad.client").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string name = System.IO.Path.GetFileName(f);
                if (name.Contains("ShaderCache", OIC)) continue;   // dev-only shader blobs, never mod content
                // "Aatrox.en_US.wad.client" → locale VO/audio companion of "Aatrox.wad.client"
                bool locale = splitLocale && Regex.IsMatch(name, @"\.[a-z]{2}_[A-Z]{2}\.wad\.client$");
                long size = 0; try { size = new FileInfo(f).Length; } catch { }
                result.Add(new GameWad(f, locale ? group + " (voice/locale)" : group,
                    name[..^".wad.client".Length], size));
            }
        }

        AddDir(System.IO.Path.Combine(final, "Champions"), "Champions", splitLocale: true);
        AddDir(System.IO.Path.Combine(final, "Maps", "Shipping"), "Maps", splitLocale: true);
        AddDir(final, "Core & UI", splitLocale: true);
        return result;
    }

    private static string? FindFinalDir(string? gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory)) return null;
        string[] candidates =
        {
            gameDirectory,
            System.IO.Path.Combine(gameDirectory, "DATA", "FINAL"),
            System.IO.Path.Combine(gameDirectory, "Game", "DATA", "FINAL"),
        };
        return candidates.FirstOrDefault(c => File.Exists(System.IO.Path.Combine(c, "DATA.wad.client")));
    }
}
