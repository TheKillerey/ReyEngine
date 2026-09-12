using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Settings;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>M688-M690: the small things. No view uses the obsolete Watermark; Help > What's New lists the
/// registry newest first and is the one acknowledgement; the Overlays checkbox can glow; an animated GIF
/// backdrop decodes its frames in order with their durations.</summary>
public sealed class SmallThingsTests
{
    /// <summary>A 4x3, three-frame GIF (red, green, blue at 100/200/300 ms), written by Pillow.</summary>
    private static readonly byte[] TinyGif = { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x04, 0x00, 0x03, 0x00, 0x81, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x21, 0xFF, 0x0B, 0x4E, 0x45, 0x54, 0x53, 0x43, 0x41, 0x50, 0x45, 0x32, 0x2E, 0x30, 0x03, 0x01, 0x00, 0x00, 0x00, 0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x03, 0x00, 0x00, 0x08, 0x08, 0x00, 0x01, 0x08, 0x1C, 0x48, 0x50, 0x60, 0x40, 0x00, 0x21, 0xF9, 0x04, 0x01, 0x14, 0x00, 0x01, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x03, 0x00, 0x81, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x08, 0x00, 0x01, 0x08, 0x1C, 0x48, 0x50, 0x60, 0x40, 0x00, 0x21, 0xF9, 0x04, 0x01, 0x1E, 0x00, 0x01, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x03, 0x00, 0x81, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x08, 0x00, 0x01, 0x08, 0x1C, 0x48, 0x50, 0x60, 0x40, 0x00, 0x3B };

    [Fact]
    public void AnAnimatedGifDecodesItsFramesInOrderWithTheirDurations()
    {
        using var gif = GifFrames.FromBytes(TinyGif);
        Assert.NotNull(gif);
        Assert.Equal(4, gif!.Width);
        Assert.Equal(3, gif.Height);
        Assert.Equal(3, gif.FrameCount);
        Assert.Equal(16, gif.Stride);
        Assert.Equal(100, gif.DurationMs(0));
        Assert.Equal(200, gif.DurationMs(1));
        Assert.Equal(300, gif.DurationMs(2));

        var pixels = new byte[gif.Stride * gif.Height];
        Assert.True(gif.Decode(0, pixels));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.Take(4));      // BGRA red
        Assert.True(gif.Decode(2, pixels));                                // forward two: sequential through 1
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels.Take(4));      // blue
        Assert.True(gif.Decode(1, pixels));                                // backwards: a rewind, still right
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels.Take(4));      // green
        Assert.False(gif.Decode(3, pixels));
        Assert.False(gif.Decode(0, new byte[4]));
    }

    [Fact]
    public void WhatIsNotAnAnimatedGifIsNotAPlayer()
    {
        Assert.Null(GifFrames.FromBytes(new byte[] { 1, 2, 3, 4 }));
        Assert.Null(GifFrames.FromBytes(Array.Empty<byte>()));
        Assert.True(WindowBackdrop.IsGif(@"C:\pictures\bg.GIF"));
        Assert.False(WindowBackdrop.IsGif(@"C:\pictures\bg.png"));
        Assert.False(WindowBackdrop.IsGif(null));
    }

    [Fact]
    public void TheWhatsNewListIsNewestFirstAndMarksWhatIsUnseen()
    {
        var rows = WhatsNewRows.Build(NewFeatures.Shipping, "0.4.2");
        var headers = rows.Where(r => r.IsHeader).ToList();
        Assert.Equal(NewFeatures.Shipping.Select(f => f.Version).Distinct().Count(), headers.Count);
        Assert.Equal(NewFeatures.Shipping.Count, rows.Count(r => !r.IsHeader));
        // newest first, and the first header is this build's release
        Assert.Equal(NewFeatures.CurrentVersion, headers[0].Version);
        Assert.True(headers[0].IsCurrent);
        for (int i = 1; i < headers.Count; i++) Assert.True(NewFeatures.Compare(headers[i - 1].Version, headers[i].Version) > 0);
        // new for exactly the reason the control glows
        foreach (var row in rows.Where(r => !r.IsHeader))
            Assert.Equal(NewFeatures.Compare(row.Version, "0.4.2") > 0, row.IsNew);
        Assert.Contains(rows, r => !r.IsHeader && r.IsNew);
        Assert.Contains(rows, r => !r.IsHeader && !r.IsNew);
        // a user who has seen everything sees nothing marked
        Assert.DoesNotContain(WhatsNewRows.Build(NewFeatures.Shipping, NewFeatures.CurrentVersion), r => r.IsNew);
        Assert.Equal("v" + NewFeatures.CurrentVersion + "  ·  this release", headers[0].Title);
    }

    [Fact]
    public void TheMenuOpensTheWindowAndGotItIsTheOneAcknowledgement()
    {
        var xaml = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        var code = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs");
        var window = Source("src", "ReyEngine.App", "Views", "WhatsNewWindow.axaml.cs");
        var theme = Source("src", "ReyEngine.App", "Themes", "ReyTheme.axaml");
        if (xaml is null || code is null || window is null || theme is null) return;
        Assert.Contains("Header=\"What's New…\" Click=\"OnShowWhatsNew\" Classes.newFeature=\"{Binding NewFeature[whats-new]}\"", xaml);
        Assert.Contains("new WhatsNewWindow(vm.NewFeature, () => vm.DismissNewFeaturesCommand.Execute(null))", code);
        Assert.Contains("_acknowledge?.Invoke();", window);
        // the Overlays checkbox glows for the props work, through the part the template paints
        Assert.Contains("Classes.newFeature=\"{Binding NewFeature[props-animated]}\"", xaml);
        Assert.Contains("<Style Selector=\"CheckBox.newFeature /template/ Border#PART_Border\">", theme);
        Assert.Contains(NewFeatures.Shipping, f => f.Id == "props-animated" && f.Version == "0.4.4");
    }

    /// <summary>The registry must be alive in a process where no test has called SetRegistry - which is
    /// every real process. It was not: All was initialised from Shipping, declared below it, so it was
    /// null and every glow binding threw. Reflection puts the process back into that state.</summary>
    [Fact]
    public void TheRegistryIsAliveWithoutAnyoneSettingIt()
    {
        var field = typeof(NewFeatures).GetField("_registry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        var before = field!.GetValue(null);
        try
        {
            field.SetValue(null, null);
            Assert.NotNull(NewFeatures.All);
            Assert.Same(NewFeatures.Shipping, NewFeatures.All);
            Assert.True(NewFeatures.AnyUnseen(""));
            Assert.True(NewFeatures.IsNew("look", "0.4.3"));
            Assert.False(NewFeatures.IsNew("look", NewFeatures.CurrentVersion));
            var lookup = new NewFeatureLookup();
            Assert.True(lookup["look"]);
            Assert.True(lookup.AnyUnseen);
        }
        finally { field.SetValue(null, before); }
    }

    /// <summary>M692: the asset's identity card, preview, details and note live INSIDE the inspector's
    /// Overview tab, not above the Overview / Materials / Shaders strip, and the strip shows for every
    /// asset (the card is what an asset without a body still needs).</summary>
    [Fact]
    public void TheAssetCardLivesInTheOverviewTab()
    {
        var main = Source("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        var inspector = Source("src", "ReyEngine.App", "Views", "InspectorView.axaml");
        if (main is null || inspector is null) return;
        Assert.DoesNotContain("<views:InspectorHeaderView />", main);
        Assert.DoesNotContain("Inspector.Details", main);
        int tab = inspector.IndexOf("Text=\"Overview\"", StringComparison.Ordinal);
        int card = inspector.IndexOf("<views:InspectorHeaderView />", StringComparison.Ordinal);
        int details = inspector.IndexOf("Inspector.Details", StringComparison.Ordinal);
        int mesh = inspector.IndexOf("Text=\"MESH\"", StringComparison.Ordinal);
        Assert.True(tab > 0 && card > tab && details > card && mesh > details, "card, details, then the geometry cards, all under Overview");
        Assert.Contains("IsVisible=\"{Binding Inspector.HasAsset}\"", inspector.Substring(0, card));
    }

    [Fact]
    public void NoViewUsesTheObsoleteWatermark()
    {
        var dir = Source("src", "ReyEngine.App", "Views");
        if (dir is null) return;
        var offenders = Directory.GetFiles(dir, "*.axaml").Where(f => Regex.IsMatch(File.ReadAllText(f), "\\bWatermark=")).Select(Path.GetFileName).ToList();
        Assert.True(offenders.Count == 0, string.Join(", ", offenders));
        Assert.True(Directory.GetFiles(dir, "*.axaml").Count(f => File.ReadAllText(f).Contains("PlaceholderText=")) >= 15);
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
