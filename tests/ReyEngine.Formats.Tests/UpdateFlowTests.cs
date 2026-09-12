using System;
using System.IO;
using System.Linq;
using ReyEngine.App.Services;
using ReyEngine.Core.Settings;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M681: the update flow - the changelog shown, the build chosen for the kind of install, the hand-over
/// scripts that replace a running app, and the mode the user (or an installer) picked.
/// </summary>
public sealed class UpdateFlowTests
{
    private static UpdateService.ReleaseAsset A(string name) => new(name, "https://example.invalid/" + name, 1);

    [Fact]
    public void TheZipInstallTakesTheWin64ZipAndTheMsiInstallTheMsi()
    {
        var assets = new[] { A("ReyEngine-v0.5.0-win-x64.zip"), A("ReyEngine-v0.5.0.msi"), A("Source.zip") };
        Assert.Equal("ReyEngine-v0.5.0-win-x64.zip", UpdateApplier.PickAsset(assets, UpdateApplier.InstallKind.Zip)!.Name);
        Assert.Equal("ReyEngine-v0.5.0.msi", UpdateApplier.PickAsset(assets, UpdateApplier.InstallKind.Msi)!.Name);
        // an MSI install on a release that ships no msi still takes the zip rather than nothing
        Assert.Equal("ReyEngine-v0.5.0-win-x64.zip", UpdateApplier.PickAsset(new[] { A("ReyEngine-v0.5.0-win-x64.zip") }, UpdateApplier.InstallKind.Msi)!.Name);
        Assert.Null(UpdateApplier.PickAsset(new[] { A("notes.txt") }, UpdateApplier.InstallKind.Zip));
        Assert.Null(UpdateApplier.PickAsset(null, UpdateApplier.InstallKind.Zip));
    }

    [Fact]
    public void TheChangelogReadsAsTextWithoutMarkdownSyntax()
    {
        string md = "## Highlights\n- **Props** draw through Riot's shaders (see [M676](https://x/y))\n- `skinScale` applies\n\nPlain `code` here.";
        string text = UpdateService.ChangelogToText(md);
        Assert.Contains("HIGHLIGHTS", text);
        Assert.Contains("\u2022 Props draw through Riot's shaders (see M676)", text);
        Assert.Contains("\u2022 skinScale applies", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("`", text);
        Assert.DoesNotContain("https://", text);
        Assert.Contains("No release notes", UpdateService.ChangelogToText(null));
    }

    [Fact]
    public void TheHandOverScriptsWaitForTheAppAndRestartIt()
    {
        string zip = UpdateApplier.BuildZipApplyScript(4242, @"C:\u\payload", @"C:\Apps\ReyEngine", @"C:\Apps\ReyEngine\ReyEngine.App.exe", @"C:\u\apply.log");
        Assert.Contains("$AppPid = 4242", zip);
        Assert.Contains("Wait-Process -Id $AppPid", zip);
        Assert.Contains("robocopy $Source $Target /E", zip);
        Assert.Contains("$LASTEXITCODE -lt 8", zip);
        Assert.Contains("Start-Process -FilePath $Exe", zip);
        Assert.Contains(@"'C:\Apps\ReyEngine\ReyEngine.App.exe'", zip);

        string msi = UpdateApplier.BuildMsiApplyScript(7, @"C:\u\ReyEngine.msi", @"C:\Apps\ReyEngine\ReyEngine.App.exe", @"C:\u\apply.log");
        Assert.Contains("Wait-Process -Id $AppPid", msi);
        Assert.Contains("msiexec.exe", msi);
        Assert.Contains("/passive /norestart", msi);
        Assert.Contains("3010", msi);
    }

    [Fact]
    public void AQuoteInAPathSurvivesTheScript()
    {
        string script = UpdateApplier.BuildZipApplyScript(1, @"C:\it's\payload", @"C:\it's\app", @"C:\it's\app\x.exe", @"C:\it's\log");
        Assert.Contains(@"'C:\it''s\payload'", script);
    }

    [Fact]
    public void AnExplicitModeWinsAndAnUnsetOneFallsBackToAsking()
    {
        Assert.Equal(UpdateService.ModeAuto, UpdateService.EffectiveMode("auto"));
        Assert.Equal(UpdateService.ModeManual, UpdateService.EffectiveMode("manual"));
        Assert.Equal(UpdateService.ModeAsk, UpdateService.EffectiveMode("ask"));
        // nothing chosen: the installer's default when one wrote it, else ask
        string unset = UpdateService.EffectiveMode("");
        Assert.Equal(UpdateService.InstallerAutoUpdateDefault switch { true => "auto", _ => "ask" }, unset);
        // an unknown mode is "nothing chosen" too - on a machine where the MSI wrote AutoUpdate=1 that is auto
        Assert.Equal(unset, UpdateService.EffectiveMode("nonsense"));

        Assert.Equal(1, UpdateService.IndexOfMode("auto"));
        Assert.Equal(0, UpdateService.IndexOfMode("whatever"));
        Assert.Equal("manual", UpdateService.ModeAtIndex(2));
        Assert.Equal("ask", UpdateService.ModeAtIndex(9));
    }

    [Fact]
    public void TheSettingCarriesAndDefaultsToUnchosen()
    {
        var s = new EditorSettings();
        Assert.Equal("", s.UpdateMode);
        var t = new EditorSettings();
        t.CopyFrom(new EditorSettings { UpdateMode = "auto" });
        Assert.Equal("auto", t.UpdateMode);
    }

    [Fact]
    public void BothWindowsGoThroughTheUpdateDialog()
    {
        var main = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        var about = Source("src", "ReyEngine.App", "Views", "AboutWindow.axaml.cs");
        if (main is null || about is null) return;
        Assert.Contains("await UpdateWindow.ShowAsync(this, r, mode, chosen =>", main);
        Assert.Contains("await UpdateWindow.ShowAsync(this, r, UpdateService.EffectiveMode(settings.UpdateMode), chosen =>", about);
        // the manual mode at startup is still the old prompt, by request
        Assert.Contains("if (mode == ReyEngine.App.Services.UpdateService.ModeManual && !manualCheck)", main);
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
