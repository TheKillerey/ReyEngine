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
    public void EveryWindowCarriesTheLayerAndFollowsSettingsLive()
    {
        var xaml = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        var code = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        var app = Source("src", "ReyEngine.App", "App.axaml.cs");
        var hook = Source("src", "ReyEngine.App", "Services", "WindowBackdrop.cs");
        if (xaml is null || code is null || app is null || hook is null) return;
        // M685: the layer is put under EVERY window's content by one hook, installed before the first
        // window exists; the main window no longer carries a layer of its own
        Assert.Contains("WindowBackdrop.Install();", app);
        Assert.True(app.IndexOf("WindowBackdrop.Install();", StringComparison.Ordinal) < app.IndexOf("new MainWindow", StringComparison.Ordinal));
        Assert.Contains("ContentControl.ContentProperty.Changed.AddClassHandler<Window>", hook);
        Assert.Contains("ThemeService.BackdropChanged += OnBackdropChanged;", hook);
        Assert.Contains("ThemeService.BackdropChanged -= OnBackdropChanged;", hook);
        Assert.DoesNotContain("BackdropLayer", xaml);
        Assert.DoesNotContain("ApplyBackdrop", code);
        // startup and Cancel restore the WHOLE look, not the palette alone
        Assert.Contains("ThemeService.Apply(ReyEngine.Core.Settings.EditorSettings.Load());", app);
        Assert.Contains("ThemeService.Apply(vm.Settings);", code);
    }

    /// <summary>M686: no view carries a colour of its own any more - every hex value that meant "error",
    /// "warning", "success", "accent" or "the ground" is a palette brush now, the active tab and the
    /// selected outliner row are classes styled from the palette, and the axis labels share three
    /// brushes defined once. What stays hard-coded is listed here on purpose: neutral black/white veils,
    /// the near-transparent hit-test panel, Windows' own close-button red, and the "new feature" glow
    /// (a BoxShadow cannot take a resource).</summary>
    [Fact]
    public void TheViewsCarryNoColoursOfTheirOwn()
    {
        var dir = Source("src", "ReyEngine.App", "Views");
        var theme = Source("src", "ReyEngine.App", "Themes", "ReyTheme.axaml");
        if (dir is null || theme is null) return;
        var allowed = new[] { "20000000", "33FFFFFF", "22FFFFFF", "01000000", "C42B1C", "8B7CF7" };
        var offenders = new System.Collections.Generic.List<string>();
        foreach (string file in Directory.GetFiles(dir, "*.axaml").Append(Path.Combine(Path.GetDirectoryName(dir)!, "Themes", "ReyTheme.axaml")))
        {
            string text = File.ReadAllText(file);
            // the three axis brushes are DEFINED in the theme, the one place a literal is the point
            bool isTheme = Path.GetFileName(file) == "ReyTheme.axaml";
            foreach (Match m in Regex.Matches(text, "=\"#([0-9A-Fa-f]{6,8})\""))
                if (!allowed.Contains(m.Groups[1].Value.ToUpperInvariant())
                    && !(isTheme && new[] { "E5645B", "53C67A", "4C9FE8" }.Contains(m.Groups[1].Value.ToUpperInvariant())))
                    offenders.Add(Path.GetFileName(file) + ": #" + m.Groups[1].Value);
            // the axis labels are the three shared brushes, never a literal
            foreach (Match m in Regex.Matches(text, "Text=\"[XYZ]\"[^>]*Foreground=\"([^\"]*)\""))
                Assert.Matches("^\\{DynamicResource ReyAxis[XYZ]Brush\\}$", m.Groups[1].Value);
        }
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));

        // the tab and the row: classes, not converters
        Assert.Contains("<Style Selector=\"Border.docTab.active\">", theme);
        Assert.Contains("<Style Selector=\"Border.docTabLine.active\">", theme);
        Assert.Contains("<Style Selector=\"Border.outRow.selected\">", theme);
        Assert.Contains("x:Key=\"ReyAxisXBrush\"", theme);
        var main = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        var outliner = Source("src", "ReyEngine.App", "Views", "MapOutlinerView.axaml");
        Assert.NotNull(main); Assert.NotNull(outliner);
        Assert.Contains("Classes=\"docTab\" Classes.active=\"{Binding IsActive}\"", main);
        Assert.Contains("Classes=\"docTabLine\" Classes.active=\"{Binding IsActive}\"", main);
        Assert.Equal(6, Regex.Matches(outliner!, "Classes=\"outRow\" Classes.selected=\"\\{Binding IsSelected\\}\"").Count);
        Assert.DoesNotContain("BoolToBrushConverter", main);
        Assert.DoesNotContain("BoolToBrushConverter", outliner);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir)!, "Converters", "BoolToBrushConverter.cs")));

        // every palette carries the soft fills the views now ask for
        var palettes = Source("src", "ReyEngine.App", "Themes", "Palettes");
        Assert.NotNull(palettes);
        foreach (var preset in ThemeService.Presets)
        {
            string text = File.ReadAllText(Path.Combine(palettes!, preset.Name + ".axaml"));
            foreach (string key in new[] { "ReyErrorSoft", "ReyWarningSoft", "ReySuccessSoft", "ReySuccessSoftHover", "ReyAccent2Soft", "ReyBgVeil", "ReyPanelClear" })
                Assert.Contains("x:Key=\"" + key + "\"", text);
            // the soft fill is the palette's OWN colour under an alpha, not a copy of Crimson's
            var error = Regex.Match(text, "x:Key=\"ReyError\">#([0-9A-Fa-f]{6})<").Groups[1].Value;
            var soft = Regex.Match(text, "x:Key=\"ReyErrorSoft\">#([0-9A-Fa-f]{8})<").Groups[1].Value;
            Assert.Equal(error.ToUpperInvariant(), soft.Substring(2).ToUpperInvariant());
        }
    }

    /// <summary>M685: the viewport chrome. SplitButton.vp keeps the tool buttons' size and colours for
    /// whatever next needs a split control; the flyouts and tooltips paint the palette, not Fluent's greys;
    /// the orbit/pan hint is out of the toolbar's way at the bottom right.
    ///
    /// <para>M762: the NavGrid SplitButton that used to prove this style live left the bar - its toggle and
    /// layer list moved into Show ▾ as plain menu rows (ViewportToolbarTests pins that). No control in
    /// MainWindow.axaml uses SplitButton.vp any more, so this only checks the STYLE definition now; a real
    /// on-screen exercise of it lives in ToolbarLookCard/UiProbe's "toolbar" mode.</para></summary>
    [Fact]
    public void TheViewportChromeMatchesTheToolbar()
    {
        var theme = Source("src", "ReyEngine.App", "Themes", "ReyTheme.axaml");
        var xaml = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        if (theme is null || xaml is null) return;

        int split = theme.IndexOf("<Style Selector=\"SplitButton.vp\">", StringComparison.Ordinal);
        Assert.True(split >= 0, "no SplitButton.vp style");
        string splitStyle = theme.Substring(split, theme.IndexOf("</Style>", split, StringComparison.Ordinal) - split);
        foreach (string setter in new[]
                 {
                     "Property=\"Padding\" Value=\"8,3\"", "Property=\"FontSize\" Value=\"11\"", "Property=\"MinHeight\" Value=\"0\"",
                     "Property=\"Background\" Value=\"{DynamicResource ReyPanelAltBrush}\"",
                     "Property=\"BorderBrush\" Value=\"{DynamicResource ReyBorderBrush}\"", "Property=\"CornerRadius\" Value=\"4\"",
                 })
            Assert.Contains(setter, splitStyle);
        // the template's 32px secondary and the double border at the seam are trimmed
        Assert.Contains("SplitButton.vp /template/ Button#PART_SecondaryButton", theme);
        Assert.Contains("Property=\"BorderThickness\" Value=\"1,1,0,1\"", theme);
        Assert.Contains("Property=\"BorderThickness\" Value=\"0,1,1,1\"", theme);
        // M762: NavGrid moved into Show ▾ (a CheckBox, not a SplitButton) - see ViewportToolbarTests.
        Assert.Contains("Content=\"🌿 NavGrid overlay\"", xaml);

        // popups: the menu's opaque panel colour, which every palette defines
        foreach (string selector in new[] { "FlyoutPresenter", "ToolTip" })
        {
            int at = theme.IndexOf("<Style Selector=\"" + selector + "\">", StringComparison.Ordinal);
            Assert.True(at >= 0, "no " + selector + " style");
            string style = theme.Substring(at, theme.IndexOf("</Style>", at, StringComparison.Ordinal) - at);
            Assert.Contains("Property=\"Background\" Value=\"{DynamicResource MenuFlyoutPresenterBackground}\"", style);
        }
        var dir = Source("src", "ReyEngine.App", "Themes", "Palettes");
        if (dir is not null)
            foreach (var preset in ThemeService.Presets)
                Assert.Contains("x:Key=\"MenuFlyoutPresenterBackground\"", File.ReadAllText(Path.Combine(dir, preset.Name + ".axaml")));

        // the hint bar
        int hint = xaml.IndexOf("<TextBlock Text=\"VIEWPORT\"", StringComparison.Ordinal);
        Assert.True(hint > 0);
        string before = xaml.Substring(Math.Max(0, hint - 400), 400);
        Assert.Contains("HorizontalAlignment=\"Right\" VerticalAlignment=\"Bottom\"", before);
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
