namespace ReyEngine.App;

/// <summary>M81: central app identity — version, author, repository. Bump <see cref="Version"/> per release;
/// the update checker compares it against the newest GitHub release tag.</summary>
public static class AppInfo
{
    public const string Version = "0.1.0";
    public const string Channel = "beta";
    public const string Author = "TheKillerey";
    public const string RepoOwner = "TheKillerey";
    public const string RepoName = "ReyEngine";
    public const string RepoUrl = $"https://github.com/{RepoOwner}/{RepoName}";

    public static string DisplayVersion => $"v{Version}-{Channel}";
}
