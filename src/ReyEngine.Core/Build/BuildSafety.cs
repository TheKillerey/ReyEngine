namespace ReyEngine.Core.Build;

public static class BuildSafety
{
    /// <summary>True if the path is inside a Riot/League install (we must never write there implicitly).</summary>
    public static bool IsInsideGameInstall(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        return full.Contains("/riot games/", StringComparison.OrdinalIgnoreCase)
            || full.Contains("/league of legends/", StringComparison.OrdinalIgnoreCase);
    }
}
