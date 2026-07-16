using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;

namespace ReyEngine.App.Services;

/// <summary>
/// M72: runtime theme switching. Every palette in Themes/Palettes defines the same Rey* keys (plus the
/// Fluent tint keys), and every view consumes them via DynamicResource — so swapping the application's
/// first merged resource dictionary restyles the whole editor live, no restart.
/// </summary>
public static class ThemeService
{
    /// <summary>One selectable theme. Accent/Surface are preview colours for the settings picker.</summary>
    public sealed record ThemePreset(string Name, string Tagline, string Accent, string Surface);

    public static readonly IReadOnlyList<ThemePreset> Presets = new[]
    {
        new ThemePreset("Crimson", "Near-black · red accent", "#E5484D", "#141417"),
        new ThemePreset("Kalista", "Deep navy · cyan accent", "#36E2C2", "#111826"),
        new ThemePreset("Violet",  "Charcoal plum · violet accent", "#8B7CF7", "#15131D"),
    };

    public const string DefaultTheme = "Crimson";

    public static string Current { get; private set; } = DefaultTheme;

    /// <summary>Apply a theme by name (falls back to the default for unknown names). Safe to call any time
    /// after the Application exists; all DynamicResource consumers restyle immediately.</summary>
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
}
