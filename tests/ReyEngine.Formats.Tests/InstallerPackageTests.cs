using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M684: the per-user MSI and the release that ships it. The installer is WiX source the C#
/// build never sees, so the contract between it and the app - the registry names the updater reads, the
/// asset names the updater picks, the public property the user is told about - is pinned here.</summary>
public sealed class InstallerPackageTests
{
    [Fact]
    public void ThePackageIsPerUserWithAStableUpgradeCode()
    {
        string? wxs = Source("installer", "Package.wxs");
        if (wxs is null) return;
        Assert.Contains("Scope=\"perUser\"", wxs);
        // the upgrade code is the identity every later MSI upgrades through; it must never change
        Assert.Contains("UpgradeCode=\"662724e7-88e6-4e3c-aec5-1059ec1cb4a1\"", wxs);
        Assert.Contains("<MajorUpgrade", wxs);
        Assert.Contains("Name=\"ReyEngine\"", wxs);
        Assert.Contains("Version=\"$(var.ProductVersion)\"", wxs);
        // per-user: the files live under the profile, never under Program Files
        Assert.Contains("StandardDirectory Id=\"LocalAppDataFolder\"", wxs);
        Assert.DoesNotContain("ProgramFiles64Folder", wxs);
        Assert.DoesNotContain("ProgramFilesFolder", wxs);
    }

    [Fact]
    public void TheInstallerWritesWhatTheUpdaterReads()
    {
        string? wxs = Source("installer", "Package.wxs");
        string? service = Source("src", "ReyEngine.App", "Services", "UpdateService.cs");
        string? applier = Source("src", "ReyEngine.App", "Services", "UpdateApplier.cs");
        if (wxs is null || service is null || applier is null) return;

        // the app reads HKCU\Software\ReyEngine\{AutoUpdate, InstallKind}; the MSI writes exactly those
        Assert.Contains("OpenSubKey(@\"Software\\ReyEngine\")", service);
        Assert.Contains("ReadRegistry(\"AutoUpdate\")", service);
        Assert.Contains("ReadRegistry(\"InstallKind\")", applier);
        Assert.Contains("Key=\"Software\\ReyEngine\"", wxs);
        Assert.Contains("<RegistryValue Name=\"InstallKind\" Type=\"string\" Value=\"msi\"", wxs);
        Assert.Contains("<RegistryValue Name=\"AutoUpdate\" Type=\"string\" Value=\"[AUTOUPDATE]\"", wxs);
        // the updater compares the kind case-insensitively against "msi"
        Assert.Contains("\"msi\", StringComparison.OrdinalIgnoreCase", applier);

        // AUTOUPDATE is public (all caps, Secure) so the command line can set it, defaults to on, and
        // reads the previous install's value back so a silent upgrade keeps the choice
        Assert.Contains("<Property Id=\"AUTOUPDATE\" Value=\"1\" Secure=\"yes\">", wxs);
        Assert.Contains("RegistrySearch Id=\"ExistingAutoUpdate\" Root=\"HKCU\" Key=\"Software\\ReyEngine\" Name=\"AutoUpdate\"", wxs);
        // an unticked box clears the property; "0" must still reach the registry, or the next upgrade
        // falls back to the default and silently turns automatic updates back on
        Assert.Contains("Action=\"SetAutoUpdateOff\" Value=\"0\"", wxs);
        Assert.Contains("Condition=\"NOT AUTOUPDATE\"", wxs);
        // the checkbox on the options page is bound to the same property
        Assert.Contains("Property=\"AUTOUPDATE\" CheckBoxValue=\"1\"", wxs);
        // and the app's parser accepts the string the installer writes
        Assert.Contains("string t when int.TryParse(t, out int ti) => ti != 0", service);
    }

    [Fact]
    public void TheReleaseShipsTheMsiBesideTheZipUnderTheNamesTheUpdaterPicks()
    {
        string? yml = Source(".github", "workflows", "release.yml");
        string? applier = Source("src", "ReyEngine.App", "Services", "UpdateApplier.cs");
        if (yml is null || applier is null) return;

        Assert.Contains("dotnet build installer/ReyEngine.Installer.wixproj", yml);
        Assert.Contains("-p:ProductVersion=$version", yml);
        Assert.Contains("$version = $env:TAG.TrimStart('v')", yml);
        Assert.Contains("ReyEngine-$env:TAG-win-x64.zip", yml);
        Assert.Contains("ReyEngine-$env:TAG-win-x64.msi", yml);
        // both go to the signing artifact and to the release
        Assert.Contains("dist/${{ steps.package.outputs.name }}\n            dist/${{ steps.msi.outputs.name }}", yml.Replace("\r\n", "\n"));
        Assert.Contains("${{ steps.asset.outputs.path }}\n            ${{ steps.asset.outputs.msi }}", yml.Replace("\r\n", "\n"));
        // the picker's rules match those names
        Assert.Contains("a.Name.EndsWith(\".msi\", OIC)", applier);
        Assert.Contains("a.Name.EndsWith(\"win-x64.zip\", OIC)", applier);
    }

    /// <summary>M691: every release's assemblies must carry that release's FILE version, and the dev
    /// build's version must not drift from the app's again - v0.4.0 through v0.4.5 all shipped
    /// FileVersion 0.1.7.0, which is what let a same-versioned upgrade keep the old files.</summary>
    [Fact]
    public void TheAssembliesAreVersionedFromTheTagAndTheDevVersionFollowsTheApp()
    {
        string? yml = Source(".github", "workflows", "release.yml");
        string? props = Source("Directory.Build.props");
        if (yml is null || props is null) return;
        // the publish step derives the version from the tag and passes it to every assembly
        int publish = yml.IndexOf("dotnet publish src/ReyEngine.App/ReyEngine.App.csproj", StringComparison.Ordinal);
        Assert.True(publish > 0);
        Assert.Contains("$version = $env:TAG.TrimStart('v')", yml.Substring(0, publish));
        Assert.Contains("-p:Version=$version", yml.Substring(publish, 400));
        // and refuses to ship an exe whose file version is not the tag's
        Assert.Contains("VersionInfo.FileVersion -ne \"$version.0\"", yml);
        // dev builds: Directory.Build.props carries the same version as AppInfo
        var m = Regex.Match(props, "<Version>([0-9.]+)</Version>");
        Assert.True(m.Success, "Directory.Build.props has no <Version>");
        Assert.Equal(ReyEngine.App.AppInfo.Version, m.Groups[1].Value);
    }

    [Fact]
    public void TheMsiHandOverIsPassiveAndKeepsTheFolder()
    {
        string? applier = Source("src", "ReyEngine.App", "Services", "UpdateApplier.cs");
        if (applier is null) return;
        // passive: a progress bar and no questions - the options page must not appear mid-update, and
        // the registry search in the package keeps the AUTOUPDATE choice without the page
        Assert.Contains("/passive /norestart", applier);
        Assert.Contains("$p.ExitCode -eq 0 -or $p.ExitCode -eq 3010", applier);
    }

    [Fact]
    public void TheProjectSuppressesOnlyThePerUserIcesAndTheReinstallOne()
    {
        string? proj = Source("installer", "ReyEngine.Installer.wixproj");
        if (proj is null) return;
        var m = Regex.Match(proj, "<SuppressIces>([^<]*)</SuppressIces>");
        Assert.True(m.Success);
        var ices = m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "ICE38", "ICE61", "ICE64", "ICE91" }, ices);
        // the old product goes BEFORE the new files: with it still present at InstallFiles, the installer's
        // file-versioning rule kept every same-versioned file, and v0.4.5 left v0.4.4's files on disk
        string? wxs = Source("installer", "Package.wxs");
        Assert.NotNull(wxs);
        Assert.Contains("Schedule=\"afterInstallValidate\"", wxs);
        Assert.DoesNotContain("Schedule=\"afterInstallExecute\"", wxs);
        Assert.Contains("AllowSameVersionUpgrades=\"yes\"", wxs);
        // the launch target is a plain property the custom action formats at click time
        Assert.Contains("<Property Id=\"WixShellExecTarget\" Value=\"[INSTALLFOLDER]ReyEngine.App.exe\" />", wxs);
        Assert.Contains("WixToolset.Sdk/", proj);
        Assert.Contains("WixToolset.UI.wixext", proj);
        Assert.Contains("WixToolset.Util.wixext", proj);
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
