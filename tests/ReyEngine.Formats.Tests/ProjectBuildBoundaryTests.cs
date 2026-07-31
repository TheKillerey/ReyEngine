using System.Reflection;
using ReyEngine.App.ViewModels;

namespace ReyEngine.Formats.Tests;

public class ProjectBuildBoundaryTests
{
    [Fact]
    public void RootFolderStagingDoesNotCopyTheBuildIntoItself()
    {
        string root = Path.Combine(Path.GetTempPath(), $"rey-build-test-{Guid.NewGuid():N}");
        string build = Path.Combine(root, "Build");
        string staged = Path.Combine(build, "staged", "RootProject");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(root, "project-file.bin"), "source");
        File.WriteAllText(Path.Combine(build, "stale.wad.client"), "generated");

        try
        {
            var copy = typeof(MainWindowViewModel).GetMethod(
                "CopyTree", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(copy);

            int count = Assert.IsType<int>(copy!.Invoke(null, new object?[] { root, staged, build, null }));

            Assert.Equal(1, count);
            Assert.Equal("source", File.ReadAllText(Path.Combine(staged, "project-file.bin")));
            Assert.False(Directory.Exists(Path.Combine(staged, "Build")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
