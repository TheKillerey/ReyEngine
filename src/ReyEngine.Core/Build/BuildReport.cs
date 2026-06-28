namespace ReyEngine.Core.Build;

public enum BuildSeverity { Info, Warning, Error }

public sealed record BuildIssue(BuildSeverity Severity, string Message);

public sealed class BuildReport
{
    public string OutputPath { get; set; } = "";
    public int ChunksTotal { get; set; }
    public int ChunksCopied { get; set; }
    public int ChunksReplaced { get; set; }
    public int ChunksFailed { get; set; }
    public long OutputSize { get; set; }
    public TimeSpan Duration { get; set; }
    public string Validation { get; set; } = "";
    public List<BuildIssue> Issues { get; } = new();

    public bool Success => Issues.TrueForAll(i => i.Severity != BuildSeverity.Error);

    public void Add(BuildSeverity severity, string message) => Issues.Add(new BuildIssue(severity, message));
}
