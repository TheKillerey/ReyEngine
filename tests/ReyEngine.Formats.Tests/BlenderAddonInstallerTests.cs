using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using ReyEngine.App.Services;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M682/M841: the Blender add-on installed from the tool - which Blenders are found, where MapgeoAddon
/// goes (from a folder or a zip), that the old ReyEngine Bridge is removed, and that the window and the menu
/// reach it.</summary>
public sealed class BlenderAddonInstallerTests
{
    private static string Temp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "reyengine-blender-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A minimal MapgeoAddon package: bl_info with a version, a submodule, and the clutter a
    /// working copy has (a cache, a .git file) that must not be installed.</summary>
    private static string Package(string root, string version = "0, 5, 0")
    {
        string dir = Path.Combine(root, "src", "MapgeoAddon");
        Directory.CreateDirectory(Path.Combine(dir, "__pycache__"));
        Directory.CreateDirectory(Path.Combine(dir, "docs"));
        File.WriteAllText(Path.Combine(dir, "__init__.py"), "bl_info = {\n    \"name\": \"Rey's Mapgeo Blender Addon\",\n    \"version\": (" + version + "),\n}\n");
        File.WriteAllText(Path.Combine(dir, "reyengine_link.py"), "PROTOCOL = 3\n");
        File.WriteAllText(Path.Combine(dir, "docs", "reyengine-link-protocol.md"), "# spec\n");
        File.WriteAllText(Path.Combine(dir, "__pycache__", "reyengine_link.cpython-313.pyc"), "x");
        File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: elsewhere");
        return dir;
    }

    [Fact]
    public void BlendersAreFoundByConfigFolderAndByExecutableAndTheExeIsOptional()
    {
        string root = Temp();
        try
        {
            string config = Path.Combine(root, "cfg");
            foreach (string v in new[] { "4.3", "5.1", "junk", "4.1" }) Directory.CreateDirectory(Path.Combine(config, v));
            string programs = Path.Combine(root, "pf");
            string pf43 = Path.Combine(programs, "Blender Foundation", "Blender 4.3");
            Directory.CreateDirectory(Path.Combine(pf43, "4.3"));
            File.WriteAllText(Path.Combine(pf43, "blender.exe"), "");
            // a Steam-style install: no version in the folder name, only the <major.minor> folder beside the exe,
            // and never started, so it has no config folder yet
            string steam = Path.Combine(root, "steam", "Blender");
            Directory.CreateDirectory(Path.Combine(steam, "5.2"));
            File.WriteAllText(Path.Combine(steam, "blender.exe"), "");

            var found = BlenderAddonInstaller.Discover(config, programs, new[] { steam });
            Assert.Equal(new[] { "5.2", "5.1", "4.3", "4.1" }, found.Select(b => b.Version));   // newest first, junk skipped
            Assert.NotNull(found.Single(b => b.Version == "4.3").Executable);
            Assert.Null(found.Single(b => b.Version == "5.1").Executable);
            Assert.Equal(Path.Combine(steam, "blender.exe"), found.Single(b => b.Version == "5.2").Executable);
            Assert.Equal(Path.Combine(config, "5.2"), found.Single(b => b.Version == "5.2").ConfigDir);
            Assert.EndsWith(Path.Combine("4.3", "scripts", "addons", "MapgeoAddon"), found.Single(b => b.Version == "4.3").AddonDir);
            Assert.All(found, b => Assert.False(b.AddonInstalled));
            Assert.Empty(BlenderAddonInstaller.Discover(Path.Combine(root, "nowhere"), Path.Combine(root, "nopf"), Array.Empty<string>()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InstallingFromAFolderCopiesThePackageAndRemovesTheOldBridge()
    {
        string root = Temp();
        try
        {
            string package = Package(root);
            var install = new BlenderAddonInstaller.BlenderInstall("5.1", Path.Combine(root, "cfg", "5.1"), null);
            Directory.CreateDirectory(install.AddonsDir);
            File.WriteAllText(install.LegacyAddonFile, "bl_info = {}\nPROTOCOL = 2\n");
            Directory.CreateDirectory(Path.Combine(install.AddonsDir, "__pycache__"));
            File.WriteAllText(Path.Combine(install.AddonsDir, "__pycache__", "reyengine_bridge.cpython-313.pyc"), "x");
            Assert.True(install.LegacyBridgeInstalled);

            // the folder source as Settings resolves it
            string source = BlenderAddonInstaller.EffectiveSource(Path.GetDirectoryName(package));
            var prepared = await BlenderAddonInstaller.PrepareSourceAsync(source);
            Assert.Equal(package, prepared.Folder);
            Assert.Equal(new Version(0, 5, 0), BlenderAddonInstaller.SourceVersion(package));

            var result = await BlenderAddonInstaller.InstallAsync(install, prepared.Folder, enable: true);
            Assert.True(result.Copied);
            Assert.False(result.Enabled);   // no blender.exe: the files are in place, the tick is the user's
            Assert.Contains("Preferences", result.Message);
            Assert.Contains("reyengine_bridge.py", result.Message);
            Assert.True(install.AddonInstalled);
            Assert.True(File.Exists(Path.Combine(install.AddonDir, "reyengine_link.py")));
            Assert.True(File.Exists(Path.Combine(install.AddonDir, "docs", "reyengine-link-protocol.md")));
            Assert.False(Directory.Exists(Path.Combine(install.AddonDir, "__pycache__")));
            Assert.False(File.Exists(Path.Combine(install.AddonDir, ".git")));
            Assert.False(install.LegacyBridgeInstalled);
            Assert.Empty(Directory.GetFiles(Path.Combine(install.AddonsDir, "__pycache__"), "reyengine_bridge*"));

            Assert.Equal(new Version(0, 5, 0), install.InstalledVersion);
            Assert.True(install.AddonCurrent(new Version(0, 5, 0)));
            Assert.False(install.AddonCurrent(new Version(0, 6, 0)));
            Assert.Null(install.AddonCurrent(null));   // a URL source: not known until downloaded
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InstallingFromAReleaseZipReplacesAnOlderCopy()
    {
        string root = Temp();
        try
        {
            string package = Package(root, "0, 6, 1");
            string zip = Path.Combine(root, "MapgeoAddon-v0.6.1.zip");
            // a release zip wraps the package in MapgeoAddon/, as release_addon.py's git archive --prefix does
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (string file in Directory.GetFiles(package, "*", SearchOption.AllDirectories))
                    archive.CreateEntryFromFile(file, "MapgeoAddon/" + Path.GetRelativePath(package, file).Replace('\\', '/'));

            var install = new BlenderAddonInstaller.BlenderInstall("4.3", Path.Combine(root, "cfg", "4.3"), null);
            Directory.CreateDirectory(install.AddonDir);
            File.WriteAllText(Path.Combine(install.AddonDir, "__init__.py"), "bl_info = {\"version\": (0, 4, 0)}\n");
            File.WriteAllText(Path.Combine(install.AddonDir, "stale_module.py"), "gone after an update\n");

            var prepared = await BlenderAddonInstaller.PrepareSourceAsync(zip);
            string temp = prepared.Folder;
            try
            {
                var result = await BlenderAddonInstaller.InstallAsync(install, prepared.Folder, enable: false);
                Assert.True(result.Copied, result.Message);
            }
            finally { prepared.Cleanup(); }
            Assert.False(Directory.Exists(temp));                                      // the extraction is cleaned up
            Assert.Equal(new Version(0, 6, 1), install.InstalledVersion);
            Assert.False(File.Exists(Path.Combine(install.AddonDir, "stale_module.py")));   // replaced, not merged
            Assert.False(Directory.Exists(install.AddonDir + ".installing"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AFolderOrZipWithoutTheAddOnIsRefusedWithAReason()
    {
        string root = Temp();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            var folder = await Assert.ThrowsAsync<InvalidOperationException>(() => BlenderAddonInstaller.PrepareSourceAsync(Path.Combine(root, "empty")));
            Assert.Contains("no MapgeoAddon package", folder.Message);
            string zip = Path.Combine(root, "other.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntry("readme.txt");
            var fromZip = await Assert.ThrowsAsync<InvalidOperationException>(() => BlenderAddonInstaller.PrepareSourceAsync(zip));
            Assert.Contains("no MapgeoAddon package", fromZip.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TheDefaultSourceIsTheGitHubReleaseAndTheEnableLineSwapsTheModules()
    {
        Assert.Equal(BlenderAddonInstaller.DefaultSource, BlenderAddonInstaller.EffectiveSource(""));
        Assert.Equal(BlenderAddonInstaller.DefaultSource, BlenderAddonInstaller.EffectiveSource(null));
        Assert.Contains("github.com/TheKillerey/MapgeoAddon", BlenderAddonInstaller.DefaultSource);
        Assert.True(BlenderAddonInstaller.IsUrl(BlenderAddonInstaller.DefaultSource));
        Assert.Equal(@"D:\x", BlenderAddonInstaller.EffectiveSource("  \"D:\\x\"  "));

        Assert.Contains("addon_enable(module='MapgeoAddon')", BlenderAddonInstaller.EnableExpression);
        Assert.Contains("disable('reyengine_bridge'", BlenderAddonInstaller.EnableExpression);
        Assert.Contains("save_userpref", BlenderAddonInstaller.EnableExpression);
    }

    [Fact]
    public void TheOldBridgeAddOnIsNoLongerShipped()
    {
        var csproj = Source("src", "ReyEngine.App", "ReyEngine.App.csproj");
        if (csproj is null) return;
        Assert.DoesNotContain("reyengine_bridge", csproj);
        string? repo = RepoRoot();
        if (repo is null) return;
        Assert.False(File.Exists(Path.Combine(repo, "tools", "blender", "reyengine_bridge.py")));
        Assert.True(File.Exists(Path.Combine(repo, "docs", "reyengine-link-protocol.md")));
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
        Assert.Contains("Text=\"{Binding BlenderAddonSourceSetting}\"", settings);
        Assert.Contains("Command=\"{Binding OpenBlenderAddonSetupCommand}\"", menu);
        Assert.Contains("Install MapgeoAddon in Blender", menu);
        Assert.DoesNotContain("reyengine_bridge.py", menu);
        Assert.Contains("settings.SelectedSection = section", window);
    }

    [Fact]
    public async Task AGitCheckoutInTheAddonsFolderIsLeftAlone()
    {
        // a developer runs MapgeoAddon from their checkout (uncommitted work included): an install must not wipe it
        string root = Temp();
        try
        {
            string package = Package(root, "0, 9, 0");
            var install = new BlenderAddonInstaller.BlenderInstall("5.1", Path.Combine(root, "cfg", "5.1"), null);
            Directory.CreateDirectory(Path.Combine(install.AddonDir, ".git"));
            File.WriteAllText(Path.Combine(install.AddonDir, "__init__.py"), "bl_info = {\"version\": (0, 5, 0)}\n");
            File.WriteAllText(Path.Combine(install.AddonDir, "work_in_progress.py"), "# not committed\n");

            Assert.Contains("git working copy", BlenderAddonInstaller.ManagedByHand(install.AddonDir));
            var result = await BlenderAddonInstaller.InstallAsync(install, package, enable: false);
            Assert.False(result.Copied);
            Assert.Contains("left alone", result.Message);
            Assert.True(File.Exists(Path.Combine(install.AddonDir, "work_in_progress.py")));
            Assert.Equal(new Version(0, 5, 0), install.InstalledVersion);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AZipThatEscapesItsFolderAndAPlainHttpSourceAreRefused()
    {
        string root = Temp();
        try
        {
            string zip = Path.Combine(root, "evil.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(archive.CreateEntry("MapgeoAddon/__init__.py").Open())) w.Write("bl_info = {}\n");
                using (var w = new StreamWriter(archive.CreateEntry("../../escaped.py").Open())) w.Write("x\n");
            }
            var escape = await Assert.ThrowsAsync<InvalidOperationException>(() => BlenderAddonInstaller.PrepareSourceAsync(zip));
            Assert.Contains("outside its folder", escape.Message);

            var http = await Assert.ThrowsAsync<InvalidOperationException>(() => BlenderAddonInstaller.PrepareSourceAsync("http://example.com/MapgeoAddon.zip"));
            Assert.Contains("https", http.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) return dir.FullName;
        return null;
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
