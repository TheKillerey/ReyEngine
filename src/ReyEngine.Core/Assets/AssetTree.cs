namespace ReyEngine.Core.Assets;

/// <summary>A single chunk inside a WAD, with its (possibly resolved) path.</summary>
public sealed class WadAssetEntry
{
    public ulong PathHash { get; init; }
    public required string Path { get; set; }
    public bool IsResolved { get; set; }
    public int CompressedSize { get; init; }
    public int UncompressedSize { get; init; }
    public string Compression { get; init; } = "";
    public AssetType Type { get; set; } = AssetType.Unknown;

    public string DisplayName
    {
        get
        {
            int slash = Path.LastIndexOf('/');
            return slash < 0 ? Path : Path[(slash + 1)..];
        }
    }
}

/// <summary>A node in the asset browser tree (folder or file).</summary>
public sealed class AssetTreeNode
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsFolder { get; init; }
    public WadAssetEntry? Entry { get; init; }
    public List<AssetTreeNode> Children { get; } = new();
}

/// <summary>Builds a folder hierarchy out of a flat list of WAD entries.</summary>
public static class AssetTree
{
    public static AssetTreeNode Build(IEnumerable<WadAssetEntry> entries, string rootName)
    {
        var root = new AssetTreeNode { Name = rootName, IsFolder = true, FullPath = "" };
        var folders = new Dictionary<string, AssetTreeNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };

        foreach (var entry in entries)
        {
            var parts = entry.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parent = root;
            var acc = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                acc = acc.Length == 0 ? parts[i] : acc + "/" + parts[i];
                if (!folders.TryGetValue(acc, out var folder))
                {
                    folder = new AssetTreeNode { Name = parts[i], IsFolder = true, FullPath = acc };
                    parent.Children.Add(folder);
                    folders[acc] = folder;
                }
                parent = folder;
            }

            string leaf = parts.Length > 0 ? parts[^1] : entry.Path;
            parent.Children.Add(new AssetTreeNode
            {
                Name = leaf,
                FullPath = entry.Path,
                IsFolder = false,
                Entry = entry,
            });
        }

        Sort(root);
        return root;
    }

    private static void Sort(AssetTreeNode node)
    {
        node.Children.Sort((a, b) =>
            a.IsFolder != b.IsFolder
                ? (a.IsFolder ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var c in node.Children)
            if (c.IsFolder) Sort(c);
    }
}
