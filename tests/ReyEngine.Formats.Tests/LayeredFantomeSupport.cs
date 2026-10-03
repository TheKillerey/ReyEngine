using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Build;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;
using ReyEngine.Core.Wad;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M816: what the layered-.fantome tests share - writing a package by hand (info.json text, entries and packed WADs exactly as a
/// test wants them), running the real Export .fantome path of the view model over a project, and reading an archive back without
/// the code under test.
/// </summary>
internal static class LayeredFantomeSupport
{
    private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>Packs the files into a real <c>.wad.client</c> at <paramref name="wadPath"/> and returns its bytes.</summary>
    public static byte[] PackWad(string scratch, string wadPath, params (string Rel, byte[] Bytes)[] files)
    {
        string folder = Path.Combine(scratch, "src-" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, bytes) in files)
        {
            string path = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(wadPath)!);
        var report = WadPackService.Pack(folder, wadPath);
        if (!report.Success) throw new InvalidOperationException("the test WAD did not pack");
        return File.ReadAllBytes(wadPath);
    }

    /// <summary>Writes a package from its parts. A directory entry ends in '/'.</summary>
    public static string WriteFantome(string path, params (string Entry, byte[] Bytes)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, bytes) in entries)
        {
            var e = zip.CreateEntry(entry, entry.StartsWith("WAD", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            if (entry.EndsWith('/')) continue;
            using var s = e.Open();
            s.Write(bytes);
        }
        return path;
    }

    public static (string, byte[]) Entry(string entry, string text) => (entry, Encoding.UTF8.GetBytes(text));

    /// <summary>The names the packed paths of a WAD are known by: one per line, sorted - the harvested table of a package.</summary>
    public static (string, byte[]) Table(params string[] names) =>
        Entry(FantomeHashtables.HarvestedPath, string.Concat(names.Order(StringComparer.Ordinal).Select(n => n + "\n")));

    /// <summary>The <c>Hashtables</c> entry that lists <see cref="Table"/>, as a JSON array text.</summary>
    public const string TableManifest =
        "[{\"Path\":\"META/hashes/game.harvested.hashes.txt\",\"Category\":\"game\",\"Algorithm\":\"xxh64\",\"Bits\":64}]";

    /// <summary>Runs Project &gt; Export .fantome's core (staging, packing, the layered archive) over a project, headlessly.</summary>
    public static string Export(MainWindowViewModel vm, string output, string? author = null)
    {
        var project = vm.Project;
        Directory.CreateDirectory(project.OutputDirectory!);
        var meta = new FantomeMeta
        {
            Name = project.EffectiveModName,
            Author = author ?? (string.IsNullOrWhiteSpace(project.ModAuthor) ? "Unknown" : project.ModAuthor!),
            Version = string.IsNullOrWhiteSpace(project.ModVersion) ? "1.0.0" : project.ModVersion,
            Description = project.ModDescription ?? "",
            Heart = project.ModHeart,
            Home = project.ModHome,
        };
        var method = typeof(MainWindowViewModel).GetMethod("ExportFantomeCore", NonPublic)!;
        try
        {
            method.Invoke(vm, new object?[] { output, meta, null, project.OutputDirectory, new NoProgress(), "Export", "No WAD was produced.", "Zipping…" });
        }
        catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); }
        return output;
    }

    private sealed class NoProgress : IProgress<(double Frac, string Stage)>
    {
        public void Report((double Frac, string Stage) value) { }
    }

    public static byte[] Bytes(ZipArchive zip, string entry)
    {
        using var s = (zip.GetEntry(entry) ?? throw new InvalidOperationException("no entry " + entry)).Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static bool Has(ZipArchive zip, string entry) => zip.GetEntry(entry) is not null;

    public static string Text(ZipArchive zip, string entry) => Encoding.UTF8.GetString(Bytes(zip, entry));

    /// <summary>Every chunk of a packed WAD stored in the archive, by path hash, with the bytes it unpacks to.</summary>
    public static Dictionary<ulong, byte[]> Chunks(ZipArchive zip, string entry, string scratch)
    {
        string path = Path.Combine(scratch, "unpacked-" + Guid.NewGuid().ToString("N") + ".wad.client");
        Directory.CreateDirectory(scratch);
        File.WriteAllBytes(path, Bytes(zip, entry));
        using var wad = WadArchive.Open(path);
        return wad.Entries.ToDictionary(e => e.PathHash, e => wad.Extract(e.PathHash));
    }

    public static ulong Hash(string path) => HashAlgorithms.WadPath(path);

    /// <summary>The layer names of a package's <c>Layers</c> table, in file order.</summary>
    public static string[] LayerKeys(ZipArchive zip)
    {
        using var info = JsonDocument.Parse(Bytes(zip, "META/info.json"));
        return info.RootElement.GetProperty("Layers").EnumerateObject().Select(p => p.Name).ToArray();
    }

    /// <summary>The text of a layer's GameData in the package, exactly as info.json spells it (found without the code under test).</summary>
    public static string? GameData(ZipArchive zip, string layer) => FantomeImporterLayersTests.GameDataTextIn(Bytes(zip, "META/info.json"), layer);
}
