using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace ReyEngine.Formats.Meta;

/// <summary>Copies one or more root objects and every locally linked object from foreign bins into a
/// target bin. This is the safe primitive behind Workshop particle templates: emitters live inside the
/// system object, while child systems and material helpers can be ordinary object links in dependency
/// bins. Existing identical objects are reused; a same-hash/different-object collision is refused.</summary>
public static class BinObjectGraphImporter
{
    public sealed record Result(byte[] Bytes, int ImportedObjects, IReadOnlyList<string> AssetPaths);

    public static Result? Import(byte[] targetBin, IEnumerable<byte[]> sourceBins,
        IReadOnlyCollection<uint> rootHashes, out string? error)
    {
        error = null;
        try
        {
            var target = SafeBinTree.Parse(targetBin);
            var sources = sourceBins.Select(SafeBinTree.Parse).ToList();
            var available = new Dictionary<uint, BinTreeObject>();
            foreach (var tree in sources)
                foreach (var (hash, obj) in tree.Objects)
                    available.TryAdd(hash, obj);

            var queue = new Queue<uint>(rootHashes);
            var visited = new HashSet<uint>();
            var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int imported = 0;

            while (queue.Count > 0)
            {
                uint hash = queue.Dequeue();
                if (!visited.Add(hash)) continue;
                if (!available.TryGetValue(hash, out var source))
                {
                    if (rootHashes.Contains(hash)) { error = $"Template object 0x{hash:x8} was not found in its source bins."; return null; }
                    continue; // external shader/shared object; keep the authored link
                }

                foreach (var property in source.Properties.Values)
                    Walk(property, queue, assets);

                if (target.Objects.TryGetValue(hash, out var existing))
                {
                    if (!BinPropEquality.ObjectsEqual(existing, source))
                    { error = $"Object 0x{hash:x8} already exists in the map with different data."; return null; }
                    continue;
                }

                target.Objects[hash] = new BinTreeObject(hash, source.ClassHash,
                    source.Properties.Select(kv => BinTreeCloner.Clone(kv.Value, kv.Key)));
                imported++;
            }

            using var output = new MemoryStream();
            target.Write(output);
            var bytes = output.ToArray();
            _ = SafeBinTree.Parse(bytes);
            return new Result(bytes, imported, assets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    private static void Walk(BinTreeProperty property, Queue<uint> links, HashSet<string> assets)
    {
        switch (property)
        {
            case BinTreeObjectLink link when link.Value != 0:
                links.Enqueue(link.Value);
                break;
            case BinTreeString text when LooksLikeAsset(text.Value):
                assets.Add(Normalize(text.Value));
                break;
            case BinTreeOptional optional when optional.Value is not null:
                Walk(optional.Value, links, assets);
                break;
            case BinTreeStruct structure:
                foreach (var child in structure.Properties.Values) Walk(child, links, assets);
                break;
            case BinTreeContainer container:
                foreach (var child in container.Elements) Walk(child, links, assets);
                break;
            case BinTreeMap map:
                foreach (var pair in map) { Walk(pair.Key, links, assets); Walk(pair.Value, links, assets); }
                break;
        }
    }

    private static bool LooksLikeAsset(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('/')) return false;
        string path = value.Replace('\\', '/');
        string extension = Path.GetExtension(path);
        return extension.Equals(".tex", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tga", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".scb", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sco", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".skn", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".skl", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".anm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wpk", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        return value.Trim().Replace('\\', '/').TrimStart('/');
    }
}
