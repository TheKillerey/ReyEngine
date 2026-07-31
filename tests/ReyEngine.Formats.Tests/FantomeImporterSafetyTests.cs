using System.IO.Compression;
using System.Text;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Projects;

namespace ReyEngine.Formats.Tests;

public class FantomeImporterSafetyTests
{
    [Fact]
    public void RawEntriesCannotEscapeTheProjectRoot()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"rey-fantome-test-{Guid.NewGuid():N}");
        string projects = Path.Combine(temp, "projects");
        string archive = Path.Combine(temp, "unsafe.fantome");
        string escaped = Path.Combine(projects, "escaped.txt");
        Directory.CreateDirectory(projects);

        try
        {
            CreateArchive(archive,
                ("RAW/../../escaped.txt", "outside"),
                ("RAW/assets/safe.txt", "inside"));

            var result = FantomeImporter.Import(archive, projects, null, new HashDatabase());

            Assert.False(File.Exists(escaped));
            Assert.Equal("inside", File.ReadAllText(Path.Combine(result.RootPath, "RAW", "assets", "safe.txt")));
            Assert.Equal(1, result.RawFiles);
            Assert.Equal(1, result.FailedChunks);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void RawWadFoldersCannotEscapeTheirDestination()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"rey-fantome-test-{Guid.NewGuid():N}");
        string projects = Path.Combine(temp, "projects");
        string archive = Path.Combine(temp, "unsafe-wad.fantome");
        Directory.CreateDirectory(projects);

        try
        {
            CreateArchive(archive,
                ("WAD/Map11.wad.client/../../escaped.txt", "outside"),
                ("WAD/Map11.wad.client/assets/safe.txt", "inside"));

            var result = FantomeImporter.Import(archive, projects, null, new HashDatabase());

            Assert.False(File.Exists(Path.Combine(result.RootPath, "escaped.txt")));
            Assert.Equal("inside", File.ReadAllText(Path.Combine(result.RootPath, "Map11", "assets", "safe.txt")));
            Assert.Equal(1, result.ExtractedFiles);
            Assert.Equal(1, result.FailedChunks);
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void DotProjectNameIsSanitized()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"rey-fantome-test-{Guid.NewGuid():N}");
        string projects = Path.Combine(temp, "projects");
        string archive = Path.Combine(temp, "dot-name.fantome");
        Directory.CreateDirectory(projects);

        try
        {
            CreateArchive(archive,
                ("META/info.json", "{\"Name\":\"..\"}"),
                ("RAW/safe.txt", "inside"));

            var result = FantomeImporter.Import(archive, projects, null, new HashDatabase());

            Assert.Equal(Path.GetFullPath(projects), Directory.GetParent(result.RootPath)!.FullName);
            Assert.Equal("_", Path.GetFileName(result.RootPath));
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private static void CreateArchive(string path, params (string Path, string Text)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = zip.CreateEntry(item.Path);
            using var stream = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes(item.Text);
            stream.Write(bytes);
        }
    }
}
