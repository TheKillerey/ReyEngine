using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using ReyEngine.Core.Settings;

namespace ReyEngine.App.Services;

/// <summary>
/// M72: runtime theme switching. Every palette in Themes/Palettes defines the same Rey* keys (plus the
/// Fluent tint keys), and every view consumes them via DynamicResource — so swapping the application's
/// first merged resource dictionary restyles the whole editor live, no restart.
///
/// <para>M683: and the user's own layer on top of a palette - an accent colour of their own, a picture
/// behind the main window, and how see-through the panels over it become. The layer is a second merged
/// dictionary that overrides exactly the keys it changes, so a palette stays a palette and "reset" is
/// removing the layer. The picture itself is the main window's (see <see cref="Backdrop"/>); this class
/// only says what it should be.</para>
/// </summary>
public static class ThemeService
{
    /// <summary>One selectable theme. Accent/Surface are preview colours for the settings picker.</summary>
    public sealed record ThemePreset(string Name, string Tagline, string Accent, string Surface);

    public static readonly IReadOnlyList<ThemePreset> Presets = new[]
    {
        new ThemePreset("Crimson",  "Near-black · red accent",         "#E5484D", "#141417"),
        new ThemePreset("Kalista",  "Deep navy · cyan accent",         "#36E2C2", "#111826"),
        new ThemePreset("Violet",   "Charcoal plum · violet accent",   "#8B7CF7", "#15131D"),
        new ThemePreset("Midnight", "Ink blue · sky accent",           "#4C8DFF", "#10141F"),
        new ThemePreset("Forest",   "Moss grey · green accent",        "#3DD68C", "#121713"),
        new ThemePreset("Amber",    "Warm charcoal · amber accent",    "#FFB454", "#161312"),
        new ThemePreset("Rose",     "Plum black · rose accent",        "#F472B6", "#161216"),
        new ThemePreset("Slate",    "Neutral grey · steel accent",     "#8FB3D9", "#191C20"),
    };

    public const string DefaultTheme = "Crimson";

    public static string Current { get; private set; } = DefaultTheme;

    /// <summary>What the main window should show behind everything; null for nothing. Raised through
    /// <see cref="BackdropChanged"/> whenever it changes, live from Settings.</summary>
    public sealed record BackdropSpec(string Path, double Opacity, int Stretch);
    public static BackdropSpec? Backdrop { get; private set; }
    public static event Action<BackdropSpec?>? BackdropChanged;

    /// <summary>Apply a theme by name (falls back to the default for unknown names). Safe to call any time
    /// after the Application exists; all DynamicResource consumers restyle immediately. The user's own
    /// layer is left as it was - see <see cref="Apply(EditorSettings)"/> for the whole look.</summary>
    public static void Apply(string? name)
    {
        var preset = Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                     ?? Presets.First(p => p.Name == DefaultTheme);
        if (Application.Current is not { } app) return;

        var uri = new Uri($"avares://ReyEngine.App/Themes/Palettes/{preset.Name}.axaml");
        var include = new ResourceInclude(uri) { Source = uri };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = include;
        else merged.Add(include);
        Current = preset.Name;
    }

    /// <summary>M683: the whole look the settings describe - palette, then the user's layer, then the
    /// backdrop. What Settings applies live and what startup and Cancel restore.</summary>
    public static void Apply(EditorSettings s)
    {
        Apply(s.Theme);
        bool picture = !string.IsNullOrWhiteSpace(s.BackgroundImagePath);
        ApplyOverrides(s.ThemeAccent, picture ? Math.Clamp(s.BackgroundGlass, 0, 1) : 0);
        SetBackdrop(picture ? new BackdropSpec(s.BackgroundImagePath, Math.Clamp(s.BackgroundImageOpacity, 0, 1), s.BackgroundImageStretch) : null);
    }

    public static void SetBackdrop(BackdropSpec? spec)
    {
        if (Equals(Backdrop, spec)) return;
        Backdrop = spec;
        BackdropChanged?.Invoke(spec);
    }

    /// <summary>The user's layer: an accent of their own and/or see-through panels. Either at its neutral
    /// value (empty accent, glass 0) removes that part; both neutral removes the layer.</summary>
    public static void ApplyOverrides(string? accentHex, double glass)
    {
        if (Application.Current is not { } app) return;
        var merged = app.Resources.MergedDictionaries;
        // the layer is tracked by reference, never by position: App.axaml may merge dictionaries of its
        // own after the palette, and those are not this class's to remove
        if (_layer is not null) merged.Remove(_layer);
        _layer = BuildOverrides(app, accentHex, glass);
        if (_layer.Count > 0) merged.Add(_layer); else _layer = null;
    }

    private static ResourceDictionary? _layer;

    /// <summary>The override dictionary itself, built from the palette currently applied so the glass
    /// colours are the palette's own with less alpha. Empty when nothing is overridden.</summary>
    public static ResourceDictionary BuildOverrides(Application app, string? accentHex, double glass)
    {
        var d = new ResourceDictionary();
        if (Color.TryParse(accentHex ?? "", out var accent) && !string.IsNullOrWhiteSpace(accentHex))
        {
            var hover = Lighten(accent, 0.18);
            var deep = Mix(accent, Color.FromRgb(0, 0, 0), 0.62);
            var onAccent = Luma(accent) > 0.6 ? Color.FromRgb(0x14, 0x12, 0x10) : Color.FromRgb(0xFF, 0xF8, 0xF6);
            Put(d, "ReyAccent", accent); Put(d, "ReyAccentHover", hover); Put(d, "ReyAccentDeep", deep); Put(d, "ReyOnAccent", onAccent);
            Put(d, "ReyBorderAccent", Mix(accent, PaletteColor(app, "ReyPanel") ?? Color.FromRgb(0x14, 0x14, 0x17), 0.7));
            Put(d, "ReySelection", WithAlpha(accent, 0x33)); Put(d, "ReyHover", WithAlpha(accent, 0x14));
            Put(d, "SystemAccentColor", accent);
            Put(d, "SystemAccentColorDark1", Mix(accent, Colors.Black, 0.12)); Put(d, "SystemAccentColorDark2", Mix(accent, Colors.Black, 0.26));
            Put(d, "SystemAccentColorDark3", Mix(accent, Colors.Black, 0.40)); Put(d, "SystemAccentColorLight1", hover);
            Put(d, "SystemAccentColorLight2", Lighten(accent, 0.36)); Put(d, "SystemAccentColorLight3", Lighten(accent, 0.55));
            // the palette's brushes were built from its colours at load, so the brushes go too
            d["TabItemHeaderSelectedPipeFill"] = new SolidColorBrush(accent);
            d["TabItemHeaderForegroundSelected"] = new SolidColorBrush(accent);
            d["TextControlBorderBrushFocused"] = new SolidColorBrush(accent);
            d["TextControlBorderBrushPointerOver"] = new SolidColorBrush((Color)d["ReyBorderAccent"]!);
            d["ComboBoxBorderBrushPointerOver"] = new SolidColorBrush((Color)d["ReyBorderAccent"]!);
            d["ScrollBarThumbFillPointerOver"] = new SolidColorBrush((Color)d["ReyBorderAccent"]!);
            d["ToggleButtonBackgroundChecked"] = new SolidColorBrush(deep);
            d["ToggleButtonBackgroundCheckedPointerOver"] = new SolidColorBrush(deep);
            d["ToggleButtonForegroundChecked"] = new SolidColorBrush(hover);
        }
        if (glass > 0.001)
        {
            // the panels over the picture keep their colour and lose opacity; inputs, dialogs and the
            // window ground stay solid, so text stays readable and other windows stay themselves
            byte alpha = (byte)Math.Round(255 * (1 - 0.85 * Math.Clamp(glass, 0, 1)));
            foreach (string key in new[] { "ReyPanel", "ReyPanelAlt", "ReyHeader", "ReyCard" })
                if (PaletteColor(app, key) is { } c) d[key + "Brush"] = new SolidColorBrush(WithAlpha(c, alpha));
        }
        return d;
    }

    private static void Put(ResourceDictionary d, string key, Color c) { d[key] = c; d[key + "Brush"] = new SolidColorBrush(c); }

    /// <summary>A colour of the palette currently applied (the first merged dictionary).</summary>
    public static Color? PaletteColor(Application app, string key)
    {
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0) return null;
        return merged[0].TryGetResource(key, app.ActualThemeVariant, out var v) && v is Color c ? c : null;
    }

    // ---- colour arithmetic, public for the tests -------------------------------------------------
    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(a.A,
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    public static Color Lighten(Color c, double t) => Mix(c, Colors.White, t);
    public static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
