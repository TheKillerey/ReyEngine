namespace ReyEngine.Core;

/// <summary>Editor-side project: where the game lives, which WADs are open, hash dir.</summary>
public sealed class ReyProject
{
    public string Name { get; set; } = "Untitled";
    public string? GameDirectory { get; set; }
    public string? HashDirectory { get; set; }
    public List<string> RecentWads { get; } = new();

    public static string GuessGameDirectory()
    {
        string[] candidates =
        {
            @"C:\Riot Games\League of Legends\Game",
            @"D:\Riot Games\League of Legends\Game",
            @"C:\Program Files\Riot Games\League of Legends\Game",
        };
        foreach (var c in candidates)
            if (Directory.Exists(c)) return c;
        return "";
    }

    public string? DefaultHashDirectory
    {
        get
        {
            var local = Path.Combine(AppContext.BaseDirectory, "data", "hashes");
            return Directory.Exists(local) ? local : HashDirectory;
        }
    }
}
