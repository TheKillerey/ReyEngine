using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ReyEngine.App.ViewModels;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M735: the chosen skybox survives a rebuild of the options list.
///
/// <para>Reported as "Capture Sequence removes the skybox, so no skybox is captured". The capture writes
/// <c>.reyengine/cinematics.json</c> before its first frame; the project watcher schedules a browser
/// refresh on any write inside the project folder; the refresh rebuilds the skybox options; and the
/// rebuild ended with "select nothing". Every frame of the capture then had no sky. Saving a bin did the
/// same, the capture only made it reproducible.</para>
/// </summary>
public sealed class SkyboxSelectionTests
{
    private static string[] Options(params string[] extra) =>
        new[] { "No skybox", "Custom image\u2026" }.Concat(extra).ToArray();

    [Fact]
    public void AnUnchangedOptionListLeavesTheChosenSkyAlone()
    {
        var vm = new MeshPreviewViewModel();
        int loads = 0;
        vm.LoadSkybox = i => { loads++; return Task.FromResult<ReyEngine.App.Services.SkyboxSpec?>(null); };

        vm.SetSkyboxOptions(Options("Sky A", "Sky B"));
        vm.SelectedSkyboxIndex = 3;            // Sky B, chosen by the user
        Assert.Equal(1, loads);                // the user's pick loads once

        vm.SetSkyboxOptions(Options("Sky A", "Sky B"));   // the watcher's refresh

        Assert.Equal(3, vm.SelectedSkyboxIndex);
        Assert.Equal(1, loads);                // and nothing was re-decoded
    }

    [Fact]
    public void AReorderedCatalogueKeepsTheSameSkyByName()
    {
        var vm = new MeshPreviewViewModel();
        int loads = 0;
        vm.LoadSkybox = i => { loads++; return Task.FromResult<ReyEngine.App.Services.SkyboxSpec?>(null); };

        vm.SetSkyboxOptions(Options("Sky A", "Sky B"));
        vm.SelectedSkyboxIndex = 3;            // Sky B
        vm.SetSkyboxOptions(Options("Sky B", "Sky A", "Sky C"));   // a new sky appeared, order changed

        Assert.Equal("Sky B", vm.SkyboxOptions[vm.SelectedSkyboxIndex]);
        Assert.Equal(1, loads);                // restoring must not re-run the loader
    }

    [Fact]
    public void ASkyThatLeftTheCatalogueFallsBackToNone()
    {
        var vm = new MeshPreviewViewModel();
        vm.LoadSkybox = i => Task.FromResult<ReyEngine.App.Services.SkyboxSpec?>(null);

        vm.SetSkyboxOptions(Options("Sky A"));
        vm.SelectedSkyboxIndex = 2;
        vm.SetSkyboxOptions(Options("Sky B"));   // Sky A is gone

        Assert.Equal(0, vm.SelectedSkyboxIndex);
        Assert.Null(vm.Skybox);
    }

    /// <summary>Restoring index 1 would open the "Custom image" file dialog - mid-capture, on the UI
    /// thread. The suppression flag is what stops that, in both view models.</summary>
    [Fact]
    public void TheMapsOwnListFollowsTheSameRuleAndNeverReopensTheFileDialog()
    {
        var main = Source("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        var preview = Source("src", "ReyEngine.App", "ViewModels", "MeshPreviewViewModel.cs");
        if (main is null || preview is null) return;

        // the rebuild no longer ends by dropping the selection
        Assert.DoesNotContain("foreach (var o in _skyboxCatalog) SkyboxOptions.Add(o.Label);\n        SelectedSkyboxIndex = 0;",
            main.Replace("\r\n", "\n"));
        // an unchanged catalogue returns before touching anything
        Assert.Contains("if (same) return;", main);
        Assert.Contains("if (same) return;", preview);
        // and the restore cannot re-enter the loader
        foreach (var src in new[] { main, preview })
        {
            Assert.Contains("_suppressSkyboxReload = true;", src);
            Assert.Contains("if (_suppressSkyboxReload) return;", src);
        }
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
