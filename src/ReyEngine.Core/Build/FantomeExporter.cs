using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ReyEngine.Core.Build;

public sealed class FantomeMeta
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = "";
    public string? Heart { get; set; }   // optional URL (donate/social)
    public string? Home { get; set; }    // optional URL (homepage/releases)
}

/// <summary>
/// Writes a Fantome / cslol-manager mod package (<c>.fantome</c>): a ZIP with <c>META/info.json</c>
/// (Name/Author/Version/Description + optional Heart/Home), <c>META/image.png</c> (thumbnail),
/// <c>META/details.json</c> (cslol layer config) and the mod WAD(s) under <c>WAD/</c>.
/// </summary>
public static class FantomeExporter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private const string DetailsJson = """
    {
      "Priority": 10,
      "override_": false,
      "InnerPath": "",
      "Random": false,
      "Layers": [
        { "Name": "base", "Priority": 1, "folder_name": "WAD", "is_active": false, "Description": null }
      ],
      "layerss": "None"
    }
    """;

    public static void Export(FantomeMeta meta, IReadOnlyList<string> wadFiles, byte[]? thumbnailPng, string outputFantome)
    {
        var dir = Path.GetDirectoryName(outputFantome);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(outputFantome)) File.Delete(outputFantome);

        using var zip = ZipFile.Open(outputFantome, ZipArchiveMode.Create);

        // META/info.json (key order mirrors a real Fantome export)
        var info = new Dictionary<string, object?>
        {
            ["Author"] = meta.Author,
            ["Description"] = meta.Description,
        };
        if (!string.IsNullOrWhiteSpace(meta.Heart)) info["Heart"] = meta.Heart;
        if (!string.IsNullOrWhiteSpace(meta.Home)) info["Home"] = meta.Home;
        info["Name"] = meta.Name;
        info["Version"] = meta.Version;
        WriteText(zip, "META/info.json", JsonSerializer.Serialize(info, Json));

        WriteText(zip, "META/details.json", DetailsJson);

        if (thumbnailPng is { Length: > 0 })
        {
            var entry = zip.CreateEntry("META/image.png", CompressionLevel.NoCompression);
            using var s = entry.Open();
            s.Write(thumbnailPng, 0, thumbnailPng.Length);
        }

        foreach (var wad in wadFiles)
        {
            if (!File.Exists(wad)) continue;
            // WAD chunks are already Zstd-compressed — store the file rather than re-deflating it.
            zip.CreateEntryFromFile(wad, "WAD/" + Path.GetFileName(wad), CompressionLevel.NoCompression);
        }
    }

    private static void WriteText(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }
}
