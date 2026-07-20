using System.Globalization;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;

namespace ReyEngine.Core.Assets;

/// <summary>Where a mounted asset comes from, in priority order (lower value = higher priority).</summary>
public enum AssetSourceKind
{
    ProjectOverride = 0, // editable workspace files (highest priority)
    ProjectFolder = 1,   // editable unpacked-WAD folder (loose/decompressed files override its packed WAD)
    ProjectWad = 2,      // editable mod .wad.client
    RiotReference = 3,   // read-only Riot source/reference WAD (lowest priority)
}

/// <summary>A source of assets that can be mounted into the virtual file system.</summary>
public interface IAssetMount : IDisposable
{
    string Name { get; }
    string Location { get; }
    AssetSourceKind Kind { get; }
    bool IsEditable { get; }
    IEnumerable<MountedAsset> Enumerate();
    bool Contains(ulong pathHash);
    /// <summary>The single asset for a hash if this mount holds it (for fallback resolution).</summary>
    MountedAsset? Get(ulong pathHash);
    byte[] Read(ulong pathHash);
    /// <summary>M74: the real on-disk file behind a hash, when this mount is file-backed (folder/override
    /// mounts). False for archive-backed mounts — their chunks have no standalone file to operate on.</summary>
    bool TryGetFilePath(ulong pathHash, out string filePath);
}

/// <summary>One asset resolved across all mounts: the winning source plus any shadowed sources.</summary>
public sealed class MountedAsset
{
    public required ulong PathHash { get; init; }
    public required string VirtualPath { get; set; }
    public bool IsResolved { get; set; }
    public AssetType Type { get; set; } = AssetType.Unknown;
    public long Size { get; set; }
    public IAssetMount Source { get; set; } = null!;
    public List<IAssetMount> AllSources { get; } = new();

    public AssetSourceKind SourceKind => Source.Kind;
    public bool IsEditable => Source.IsEditable;
    public bool HasConflict => AllSources.Count > 1;

    public WadAssetEntry ToEntry() => new()
    {
        PathHash = PathHash,
        Path = VirtualPath,
        IsResolved = IsResolved,
        Type = Type,
        UncompressedSize = (int)Math.Min(Size, int.MaxValue),
        SourceKind = SourceKind,
        ReadOnly = !IsEditable,
        HasConflict = HasConflict,
    };
}

/// <summary>Mounts a .wad.client (project mod WAD or read-only Riot reference).</summary>
public sealed class WadMount : IAssetMount
{
    private readonly WadArchive _archive;

    public string Name { get; }
    public string Location => _archive.FilePath;
    public AssetSourceKind Kind { get; }
    public bool IsEditable { get; }
    public WadArchive Archive => _archive;

    public WadMount(WadArchive archive, AssetSourceKind kind, bool editable, string? name = null)
    {
        _archive = archive;
        Kind = kind;
        IsEditable = editable;
        Name = name ?? archive.Name;
    }

    public IEnumerable<MountedAsset> Enumerate()
    {
        foreach (var e in _archive.Entries)
            yield return new MountedAsset
            {
                PathHash = e.PathHash,
                VirtualPath = e.Path,
                IsResolved = e.IsResolved,
                Type = e.Type,
                Size = e.UncompressedSize,
                Source = this,
            };
    }

    public bool Contains(ulong pathHash) => _archive.TryGetEntry(pathHash, out _);

    public MountedAsset? Get(ulong pathHash) => _archive.TryGetEntry(pathHash, out var e)
        ? new MountedAsset { PathHash = e.PathHash, VirtualPath = e.Path, IsResolved = e.IsResolved, Type = e.Type, Size = e.UncompressedSize, Source = this }
        : null;

    public byte[] Read(ulong pathHash) => _archive.Extract(pathHash);
    public bool TryGetFilePath(ulong pathHash, out string filePath) { filePath = ""; return false; }
    public void Dispose() => _archive.Dispose();
}

/// <summary>
/// Mounts an unpacked-WAD folder (the cslol/mod format): files live at their resolved WAD path
/// (e.g. <c>ASSETS/...</c>, <c>DATA/...</c>); loose <c>&lt;hash&gt;.ext</c> files are unresolved chunks.
/// </summary>
public sealed class FolderMount : IAssetMount
{
    private readonly Dictionary<ulong, string> _files = new();
    private readonly List<string> _dirs = new();
    private readonly IHashResolver? _resolver;

    /// <summary>M110: every directory under the mount, relative and '/'-separated — INCLUDING empty ones.
    /// The file index alone can't represent a folder you just created, so without this a new (or emptied)
    /// folder is invisible in the browser even though it exists on disk.</summary>
    public IReadOnlyList<string> Directories => _dirs;

    public string Name { get; }
    public string Location { get; }
    public AssetSourceKind Kind => AssetSourceKind.ProjectFolder;
    public bool IsEditable => true;

    public FolderMount(string root, IHashResolver? resolver, string? name = null)
    {
        Location = root;
        Name = name ?? Path.GetFileName(root.TrimEnd('/', '\\'));
        _resolver = resolver;
        Index();
    }

    private void Index()
    {
        foreach (var dir in Directory.EnumerateDirectories(Location, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Location, dir).Replace('\\', '/');
            if (rel.StartsWith(".reyengine", StringComparison.OrdinalIgnoreCase)) continue;
            _dirs.Add(rel);
        }

        foreach (var file in Directory.EnumerateFiles(Location, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Location, file).Replace('\\', '/');
            if (rel.Equals("hashed_bins.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith(".reyengine/", StringComparison.OrdinalIgnoreCase)) continue;

            ulong hash;
            string name = Path.GetFileNameWithoutExtension(rel);
            if (!rel.Contains('/') && name.Length == 16 && ulong.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
                hash = h;                       // loose <hash>.ext chunk
            else
                hash = HashAlgorithms.WadPath(rel);
            _files[hash] = file;
        }
    }

    public IEnumerable<MountedAsset> Enumerate()
    {
        foreach (var (hash, file) in _files) yield return BuildAsset(hash, file);
    }

    private MountedAsset BuildAsset(ulong hash, string file)
    {
        var rel = Path.GetRelativePath(Location, file).Replace('\\', '/');
        string name = Path.GetFileNameWithoutExtension(rel);
        bool looseHash = !rel.Contains('/') && name.Length == 16;

        string path = rel;
        bool resolved = !looseHash;
        if (looseHash && _resolver is not null && _resolver.TryGetPath(hash, out var rp)) { path = rp; resolved = true; }
        else if (looseHash) path = $"0x{hash:x16}{Path.GetExtension(rel)}";

        return new MountedAsset
        {
            PathHash = hash,
            VirtualPath = path,
            IsResolved = resolved,
            Type = AssetTypeDetector.FromPath(path),
            Size = new FileInfo(file).Length,
            Source = this,
        };
    }

    public bool Contains(ulong pathHash) => _files.ContainsKey(pathHash);
    public MountedAsset? Get(ulong pathHash) => _files.TryGetValue(pathHash, out var f) ? BuildAsset(pathHash, f) : null;
    public byte[] Read(ulong pathHash) => File.ReadAllBytes(_files[pathHash]);
    public bool TryGetFilePath(ulong pathHash, out string filePath) => _files.TryGetValue(pathHash, out filePath!);
    public void Dispose() { }
}

/// <summary>Mounts the project's override workspace: files named <c>&lt;hash&gt;.ext</c>.</summary>
public sealed class OverrideMount : IAssetMount
{
    private readonly Dictionary<ulong, string> _files = new();
    private readonly IHashResolver? _resolver;

    public string Name => "Overrides";
    public string Location { get; }
    public AssetSourceKind Kind => AssetSourceKind.ProjectOverride;
    public bool IsEditable => true;

    public OverrideMount(string overridesDir, IHashResolver? resolver)
    {
        Location = overridesDir;
        _resolver = resolver;
        if (!Directory.Exists(overridesDir)) return;
        foreach (var file in Directory.EnumerateFiles(overridesDir))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length == 16 && ulong.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
                _files[h] = file;
        }
    }

    public IEnumerable<MountedAsset> Enumerate()
    {
        foreach (var (hash, file) in _files) yield return BuildAsset(hash, file);
    }

    private MountedAsset BuildAsset(ulong hash, string file)
    {
        bool resolved = false;
        string path;
        if (_resolver is not null && _resolver.TryGetPath(hash, out var rp)) { resolved = true; path = rp; }
        else path = $"0x{hash:x16}{Path.GetExtension(file)}";
        return new MountedAsset
        {
            PathHash = hash,
            VirtualPath = path,
            IsResolved = resolved,
            Type = AssetTypeDetector.FromPath(path),
            Size = new FileInfo(file).Length,
            Source = this,
        };
    }

    public bool Contains(ulong pathHash) => _files.ContainsKey(pathHash);
    public MountedAsset? Get(ulong pathHash) => _files.TryGetValue(pathHash, out var f) ? BuildAsset(pathHash, f) : null;
    public byte[] Read(ulong pathHash) => File.ReadAllBytes(_files[pathHash]);
    public bool TryGetFilePath(ulong pathHash, out string filePath) => _files.TryGetValue(pathHash, out filePath!);
    public void Dispose() { }
}
