using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media;
using ReyEngine.App.Services;
using ReyEngine.Core.Settings;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M683: more palettes, an accent of the user's own, and a picture behind the editor.</summary>
public sealed class ThemeLookTests
{
    [Fact]
    public void EveryPresetHasAPaletteFileWithCrimsonsWholeKeySet()
    {
        var dir = Source("src", "ReyEngine.App", "Themes", "Palettes");
        if (dir is null) return;
        var crimson = File.ReadAllText(Path.Combine(dir, "Crimson.axaml"));
        var keys = Regex.Matches(crimson, "x:Key=\"([A-Za-z0-9]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(keys.Count > 60);
        Assert.True(ThemeService.Presets.Count >= 8);
        foreach (var preset in ThemeService.Presets)
        {
            string file = Path.Combine(dir, preset.Name + ".axaml");
            Assert.True(File.Exists(file), preset.Name + " has no palette file");
            string text = File.ReadAllText(file);
            var missing = keys.Where(k => !text.Contains("x:Key=\"" + k + "\"")).ToList();
            Assert.True(missing.Count == 0, preset.Name + " lacks " + string.Join(", ", missing));
            // the preview swatch is the palette's own accent
            Assert.Contains("<Color x:Key=\"ReyAccent\">" + preset.Accent + "</Color>", text);
        }
    }

    [Fact]
    public void TheColourArithmeticStaysInRange()
    {
        var c = Color.Parse("#E5484D");
        Assert.Equal(0x33, ThemeService.WithAlpha(c, 0x33).A);
        Assert.Equal(Colors.White.R, ThemeService.Lighten(c, 1).R);
        Assert.Equal(c.R, ThemeService.Mix(c, Colors.Black, 0).R);
        Assert.Equal(0, ThemeService.Mix(c, Colors.Black, 1).R);
        Assert.InRange(ThemeService.Luma(c), 0, 1);
        Assert.True(ThemeService.Luma(Colors.White) > ThemeService.Luma(Colors.Black));
    }

    [Fact]
    public void TheLookSettingsCarryAndDefaultToNoPicture()
    {
        var s = new EditorSettings();
        Assert.Equal("", s.ThemeAccent);
        Assert.Equal("", s.BackgroundImagePath);
        Assert.InRange(s.BackgroundImageOpacity, 0.05, 0.95);
        var t = new EditorSettings();
        t.CopyFrom(new EditorSettings { ThemeAccent = "#123456", BackgroundImagePath = "x.png", BackgroundImageOpacity = 0.7, BackgroundGlass = 0.2, BackgroundImageStretch = 3 });
        Assert.Equal("#123456", t.ThemeAccent);
        Assert.Equal("x.png", t.BackgroundImagePath);
        Assert.Equal(0.7, t.BackgroundImageOpacity);
        Assert.Equal(0.2, t.BackgroundGlass);
        Assert.Equal(3, t.BackgroundImageStretch);
    }

    /// <summary>Picking a palette card moves the accent picker to THAT palette's accent - measured
    /// headless, the first cut set the picker before <c>_theme</c> moved, so it showed the palette
    /// picked before. A custom accent stays where the user put it.</summary>
    [Fact]
    public void PickingAPaletteMovesThePickerToItsOwnAccent()
    {
        var vm = new ReyEngine.App.ViewModels.SettingsViewModel(new EditorSettings());
        foreach (var item in vm.Themes)
        {
            vm.SelectThemeCommand.Execute(item);
            var preset = ThemeService.Presets.Single(p => p.Name == item.Name);
            Assert.False(vm.AccentIsCustom);
            Assert.Equal(Color.Parse(preset.Accent), vm.AccentColor);
            Assert.Equal(preset.Name, vm.LookSettings().Theme);
            Assert.Equal("", vm.LookSettings().ThemeAccent);
        }

        vm.AccentColor = Colors.DodgerBlue;
        Assert.True(vm.AccentIsCustom);
        vm.SelectThemeCommand.Execute(vm.Themes.First(t => t.Name == "Forest"));
        Assert.Equal(Colors.DodgerBlue, vm.AccentColor);
        Assert.Equal("#1E90FF", vm.LookSettings().ThemeAccent);
        vm.ResetAccentCommand.Execute(null);
        Assert.False(vm.AccentIsCustom);
        Assert.Equal(Color.Parse(ThemeService.Presets.Single(p => p.Name == "Forest").Accent), vm.AccentColor);
    }

    [Fact]
    public void TheMainWindowCarriesTheLayerAndFollowsSettingsLive()
    {
        var xaml = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        var code = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        var app = Source("src", "ReyEngine.App", "App.axaml.cs");
        if (xaml is null || code is null || app is null) return;
        Assert.Contains("<Border x:Name=\"BackdropLayer\" IsHitTestVisible=\"False\" />", xaml);
        Assert.Contains("ThemeService.BackdropChanged +=", code);
        // startup and Cancel restore the WHOLE look, not the palette alone
        Assert.Contains("ThemeService.Apply(ReyEngine.Core.Settings.EditorSettings.Load());", app);
        Assert.Contains("ThemeService.Apply(vm.Settings);", code);
    }

    private static string? Source(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path) || Directory.Exists(path)) return path.EndsWith(".cs") || path.EndsWith(".axaml") ? File.ReadAllText(path) : path;
        }
        return null;
    }
}
