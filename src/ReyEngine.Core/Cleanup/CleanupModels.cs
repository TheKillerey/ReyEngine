using ReyEngine.Core.Assets;

namespace ReyEngine.Core.Cleanup;

/// <summary>Why a file turned up in a cleanup scan. Also the UI's result grouping.</summary>
public enum CleanupGroup
{
    /// <summary>Nothing in the project or the game can ever ask for this file.</summary>
    Unused = 0,
    /// <summary>Byte- or content-identical to the Riot original, so deleting it falls back to Riot.</summary>
    IdenticalToRiot = 1,
    /// <summary>A directory with no files anywhere beneath it.</summary>
    EmptyFolder = 2,
    /// <summary>Deliberately kept, or not confidently judged. NEVER selected by default.</summary>
    Protected = 3,
}

/// <summary>One row of a cleanup preview. Produced by scanning only - nothing is touched.</summary>
public sealed record CleanupCandidate(
    CleanupGroup Group,
    string RelPath,
    string AbsPath,
    string Folder,
    AssetType Type,
    long Bytes,
    string Reason)
{
    /// <summary>Uncertain and protected rows must never arrive pre-ticked - the whole safety story of
    /// this tool is that the default selection cannot destroy anything the scan was unsure about.</summary>
    public bool SelectedByDefault => Group is CleanupGroup.Unused or CleanupGroup.IdenticalToRiot
                                                or CleanupGroup.EmptyFolder;
}

public sealed record CleanupReport(
    string ProjectPath,
    bool RiotReferenceAvailable,
    string RiotStatus,
    IReadOnlyList<CleanupCandidate> Candidates,
    int FilesScanned,
    long BytesScanned,
    IReadOnlyList<string> Notes)
{
    public static CleanupReport Empty(string path) =>
        new(path, false, "No project open.", Array.Empty<CleanupCandidate>(), 0, 0, Array.Empty<string>());
}

/// <summary>One removed file, recorded so it can be put back.</summary>
public sealed class CleanupManifestEntry
{
    public string OriginalPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public long Bytes { get; set; }
    public string Reason { get; set; } = "";
    public string Group { get; set; } = "";
    /// <summary>SHA-256 of the bytes as they were when moved - proves a restore puts back the same file.</summary>
    public string Sha256 { get; set; } = "";
    public string RemovedUtc { get; set; } = "";
}

/// <summary>The record of one cleanup run, written BEFORE anything is moved.</summary>
public sealed class CleanupManifest
{
    public string Id { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public List<CleanupManifestEntry> Entries { get; set; } = new();
    public List<string> EmptyFolders { get; set; } = new();
    /// <summary>Set once the files have been put back, so a manifest is not restored twice.</summary>
    public bool Restored { get; set; }
}

/// <summary>
/// The one place that answers "does anything in this project point at this file?". Implemented over the
/// project's bins (which is where League keeps every asset reference), so the cleanup tool does not grow
/// a second, divergent notion of what "referenced" means.
/// </summary>
public interface IReferenceIndex
{
    /// <summary>True if any project content can reach this file. <paramref name="how"/> names the match
    /// that saved it, so a preview can explain itself rather than just asserting.</summary>
    bool IsReferenced(string relPath, ulong pathHash, out string how);
}
