using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ReyEngine.App.Services;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M682: the Blender add-on installed from the tool - which Blenders are found, where the file
/// goes, what the build ships, and that the window and the menu reach it.</summary>
public sealed class BlenderAddonInstallerTests
{
    private static string Temp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "reyengine-blender-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void EveryVersionFolderIsABlenderAndTheExeIsOptional()
    {
        string root = Temp();
        try
        {
            string config = Path.Combine(root, "cfg");
            foreach (string v in new[] { "4.3", "5.1", "junk", "4.1" }) Directory.CreateDirectory(Path.Combine(config, v));
            string programs = Path.Combine(root, "pf");
            Directory.CreateDirectory(Path.Combine(programs, "Blender Foundation", "Blender 4.3"));
            File.WriteAllText(Path.Combine(programs, "Blender Foundation", "Blender 4.3", "blender.exe"), "");

            var found = BlenderAddonInstaller.Discover(config, programs);
            Assert.Equal(new[] { "5.1", "4.3", "4.1" }, found.Select(b => b.Version));   // newest first, junk skipped
            Assert.NotNull(found.Single(b => b.Version == "4.3").Executable);
            Assert.Null(found.Single(b => b.Version == "5.1").Executable);
            Assert.EndsWith(Path.Combine("4.3", "scripts", "addons"), found.Single(b => b.Version == "4.3").AddonsDir);
            Assert.All(found, b => Assert.False(b.AddonInstalled));
            Assert.Empty(BlenderAddonInstaller.Discover(Path.Combine(root, "nowhere"), programs));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InstallCopiesTheFileAndReportsCurrent()
    {
        string root = Temp();
        try
        {
            string shipped = Path.Combine(root, "reyengine_bridge.py");
            File.WriteAllText(shipped, "bl_info = {}\n");
            var install = new BlenderAddonInstaller.BlenderInstall("4.3", Path.Combine(root, "cfg", "4.3"), null);

            var result = await BlenderAddonInstaller.InstallAsync(install, shipped, enable: true);
            Assert.True(result.Copied);
            Assert.False(result.Enabled);   // no blender.exe: the file is in place, the tick is the user's
            Assert.Contains("Preferences", result.Message);
            Assert.True(install.AddonInstalled);
            Assert.True(install.AddonCurrent(shipped));

            File.WriteAllText(shipped, "bl_info = {'version': (1, 1, 0)}\n");
            Assert.False(install.AddonCurrent(shipped));   // an older copy is not current
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TheBuildShipsTheAddonAndTheEnableLineNamesTheModule()
    {
        // from the test output folder the repository's tools/blender copy is found by walking up
        Assert.NotNull(BlenderAddonInstaller.ShippedAddonPath());
        Assert.Contains("addon_enable(module='reyengine_bridge')", BlenderAddonInstaller.EnableExpression);
        Assert.Contains("save_userpref", BlenderAddonInstaller.EnableExpression);

        var csproj = Source("src", "ReyEngine.App", "ReyEngine.App.csproj");
        if (csproj is null) return;
        Assert.Contains("Link=\"blender\\reyengine_bridge.py\"", csproj);
    }

    [Fact]
    public void TheWindowAndTheMenuReachTheSection()
    {
        var settings = Source("src", "ReyEngine.App", "Views", "SettingsWindow.axaml");
        var menu = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        var window = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        if (settings is null || menu is null || window is null) return;
        Assert.Contains("IsVisible=\"{Binding ShowBlender}\"", settings);
        Assert.Contains("Command=\"{Binding InstallBlenderEverywhereCommand}\"", settings);
        Assert.Contains("Command=\"{Binding OpenBlenderAddonSetupCommand}\"", menu);
        Assert.Contains("settings.SelectedSection = section", window);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }
}
