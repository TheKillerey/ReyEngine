using System.Text;
using ReyEngine.Core.Build;
using ReyEngine.Formats.LtkGameData;

namespace ReyEngine.Formats.Tests;

/// <summary>A temporary folder that is removed with the object.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rey-m818-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// M818: what the object-index and overlay tests share: bins and PTCHs made of a few objects, a synthetic installation written as real <c>.wad.client</c> files by the app's own packer, a synthetic
/// mod, and the documents that name them. Nothing here needs the installed game.
/// </summary>
internal static class OverlayKit
{
    public static uint H(string name) => LtkHash.Fnv1aLower(name);

    public static readonly uint TestClass = H("TestClass");

    public static PropValue Strings(params string[] values) =>
        new PropList(PropKind.Container, PropKind.String, values.Select(v => (PropValue)new PropString(v)).ToList());

    /// <summary>An object <paramref name="path"/> of <c>TestClass</c> holding <c>tags</c> (a list of strings), <c>name</c> and <c>count</c>.</summary>
    public static PropObject Obj(string path, string[] tags, string? name = null, int count = 1)
    {
        var obj = new PropObject(H(path), TestClass);
        obj.Properties.Set(H("tags"), Strings(tags));
        if (name is not null) obj.Properties.Set(H("name"), new PropString(name));
        obj.Properties.Set(H("count"), new PropInt(PropKind.I32, (ulong)(uint)count));
        return obj;
    }

    public static byte[] Bin(string[]? dependencies, params PropObject[] objects)
    {
        var bin = new PropBin();
        if (dependencies is not null) bin.Dependencies.AddRange(dependencies);
        foreach (var o in objects) bin.Objects.Set(o.PathHash, o);
        return PropCodec.WriteProp(bin);
    }

    public static byte[] Bin(params PropObject[] objects) => Bin(null, objects);

    /// <summary>A PROP of version 2: the same layout as version 3, which the engine writes.</summary>
    public static byte[] V2(byte[] v3)
    {
        var copy = (byte[])v3.Clone();
        copy[4] = 2;
        return copy;
    }

    public static byte[] Ptch(PropObject[] objects, params uint[] deleted)
    {
        var patch = new PtchBin();
        foreach (var o in objects) patch.Objects.Set(o.PathHash, o);
        patch.Deleted.AddRange(deleted);
        return PropCodec.WritePtch(patch);
    }

    /// <summary>A PROP whose table of objects lists two rows of one path hash: the objects of two one-object bins joined under one header.</summary>
    public static byte[] TwoRows(PropObject first, PropObject second)
    {
        byte[] one = Bin(first), two = Bin(second);
        // PROP, version, 0 dependencies, the object count and the classes make 20 bytes in a bin of one object; after them come the objects
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("PROP"));
        bytes.AddRange(BitConverter.GetBytes(3u));
        bytes.AddRange(BitConverter.GetBytes(0u));
        bytes.AddRange(BitConverter.GetBytes(2u));
        bytes.AddRange(BitConverter.GetBytes(first.ClassHash));
        bytes.AddRange(BitConverter.GetBytes(second.ClassHash));
        bytes.AddRange(one.AsSpan(20).ToArray());
        bytes.AddRange(two.AsSpan(20).ToArray());
        return bytes.ToArray();
    }

    /// <summary>A declaration document: the modules, each with the origin the document gives it by position.</summary>
    public static string Doc(params string[] modules) =>
        "{\"version\":1,\"modules\":[" + string.Join(",", modules.Select((m, i) =>
            m.Replace("\"origin\":MODULE", $"\"origin\":{{\"manifest\":\"game_data.yaml\",\"source\":null,\"module\":{i}}}"))) + "]}";

    public static string Target(string path, string edits, string? name = null) =>
        $"{{{(name is null ? "" : $"\"name\":\"{name}\",")}\"target\":\"{path}\",\"edits\":[{edits}],\"origin\":MODULE}}";

    public static string Entries(string entries) => $"{{\"entries\":{{{entries}}},\"origin\":MODULE}}";

    /// <summary>The tags of the object <paramref name="path"/> in a bin's bytes.</summary>
    public static string[] TagsOf(byte[] bytes, string path)
    {
        var obj = PropCodec.ReadObject(bytes, H(path)) ?? throw new InvalidOperationException("no object " + path);
        return ((PropList)obj.Properties.ValueAt(obj.Properties.IndexOf(H("tags")))).Items.Select(i => ((PropString)i).Value).ToArray();
    }

    /// <summary>Whether the chunk of a path in a synthetic game holds the hash a path gives it.</summary>
    public static ulong ChunkOf(string path) => LtkHash.Xxh64Path(path);

    /// <summary>A chunk read with no limit a test cares about: the reader's own ceiling (a gigabyte) applies, and no token.</summary>
    public static byte[] Read(this IGameChunkReader reader, int archive, ulong chunk) => reader.ReadChunk(archive, chunk, long.MaxValue, CancellationToken.None);
}

/// <summary>A synthetic installation: archives of chunks, written as real <c>.wad.client</c> files.</summary>
internal sealed class SyntheticGame
{
    private readonly SortedDictionary<string, List<(ulong Chunk, byte[] Bytes)>> _archives = new(StringComparer.Ordinal);
    private readonly List<(string Archive, byte[] Bytes)> _broken = new();

    public SyntheticGame Add(string archive, string chunkPath, byte[] bytes) => AddHash(archive, LtkHash.Xxh64Path(chunkPath), bytes);

    public SyntheticGame AddHash(string archive, ulong hash, byte[] bytes)
    {
        if (!_archives.TryGetValue(archive, out var list)) _archives[archive] = list = new();
        list.Add((hash, bytes));
        return this;
    }

    /// <summary>A file named like an archive that is not one.</summary>
    public SyntheticGame AddBroken(string archive, byte[] bytes)
    {
        _broken.Add((archive, bytes));
        return this;
    }

    /// <summary>Writes <c>&lt;gameDirectory&gt;/DATA/FINAL/...</c>.</summary>
    public string Write(string gameDirectory)
    {
        string root = Path.Combine(gameDirectory, "DATA", "FINAL");
        Directory.CreateDirectory(root);
        foreach (var (archive, bytes) in _broken)
        {
            string broken = Path.Combine(root, archive.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
            File.WriteAllBytes(broken, bytes);
        }
        foreach (var (archive, chunks) in _archives)
        {
            string folder = Path.Combine(Path.GetTempPath(), "rey-m818-pack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                foreach (var (hash, bytes) in chunks) File.WriteAllBytes(Path.Combine(folder, $"{hash:x16}.bin"), bytes);
                string target = Path.Combine(root, archive.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var report = WadPackService.Pack(folder, target);
                if (!report.Success) throw new InvalidOperationException("the synthetic WAD did not pack: " + archive);
            }
            finally { Directory.Delete(folder, true); }
        }
        return gameDirectory;
    }
}

/// <summary>A synthetic mod: layers with their GameData, WAD folders, RAW files, override files.</summary>
internal sealed partial class SyntheticMod
{
    public readonly List<(string Name, int Priority, string? GameData)> Layers = new();
    public readonly List<(string Layer, string Wad, string Rel, byte[] Bytes)> WadFiles = new();
    public readonly List<(string Rel, byte[] Bytes)> Raw = new();
    public readonly List<(string Layer, string Path, byte[] Bytes)> LayerFiles = new();

    public SyntheticMod Layer(string name, int priority, string? gameData)
    {
        Layers.Add((name, priority, gameData));
        return this;
    }

    /// <summary>The chunk a mod's file is for: a hex-named file is the hash it spells, any other is the hash of its path.</summary>
    public static ulong HashOf(string rel)
    {
        string name = rel.Replace('\\', '/');
        string stem = Path.GetFileNameWithoutExtension(name);
        if (!name.Contains('/') && stem.Length == 16 && ulong.TryParse(stem, System.Globalization.NumberStyles.HexNumber, null, out ulong direct)) return direct;
        return LtkHash.Xxh64Path(name);
    }

    /// <summary>The layers as overlay inputs: their documents and override files.</summary>
    public IReadOnlyList<GameDataLayerInput> ToLayers() =>
        Layers.Select(l => new GameDataLayerInput(l.Name, l.Priority, l.GameData, new Files(this, l.Name))).ToList();

    /// <summary>The mod's own copies of chunks.</summary>
    public IGameDataModFiles ToFiles() => new Files(this, null);

    private sealed class Files : IGameDataLayerFiles, IGameDataModFiles
    {
        private readonly SyntheticMod _mod;
        private readonly string? _layer;

        public Files(SyntheticMod mod, string? layer) { _mod = mod; _layer = layer; }

        public byte[]? ReadOverrideFile(string path, long maxBytes) =>
            _mod.LayerFiles.FirstOrDefault(f => f.Layer == _layer && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)).Bytes;

        public byte[]? ReadLayerFile(string layer, ulong chunk, long maxBytes)
        {
            // the layer's WAD folders are one namespace: a later folder's copy replaces an earlier one's
            byte[]? found = null;
            foreach (var (l, _, rel, bytes) in _mod.WadFiles)
                if (l == layer && HashOf(rel) == chunk) found = bytes;
            return found;
        }

        public byte[]? ReadRawFile(ulong chunk, long maxBytes)
        {
            byte[]? found = null;
            foreach (var (rel, bytes) in _mod.Raw)
                if (HashOf(rel) == chunk) found = bytes;
            return found;
        }
    }
}

/// <summary>A game, a mod and the layers in play: small enough to read, and aimed at one rule of the overlay.</summary>
internal sealed record Scenario(string Name, SyntheticGame Game, SyntheticMod Mod, string[]? ActiveLayers = null);
