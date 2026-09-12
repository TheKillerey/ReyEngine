using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Settings;

namespace ReyEngine.App.Views;

/// <summary>M689: Help ▸ What's New… - every feature the registry names, newest release first, with
/// the unacknowledged ones marked, and the one "Got it" that puts every menu glow out. Built on a
/// lookup and a callback rather than the main view-model so it opens headless in a check.</summary>
public partial class WhatsNewWindow : Window
{
    private readonly Action? _acknowledge;

    public WhatsNewWindow() : this(new NewFeatureLookup(), null) { }

    public WhatsNewWindow(NewFeatureLookup lookup, Action? acknowledge)
    {
        InitializeComponent();
        _acknowledge = acknowledge;
        var rows = WhatsNewRows.Build(NewFeatures.Shipping, lookup.LastSeenVersion);
        Rows.ItemsSource = rows;
        bool anyNew = lookup.AnyUnseen;
        GotIt.IsVisible = anyNew;
        Intro.Text = anyNew
            ? $"What changed since the release you last acknowledged. The entries marked NEW are the ones whose menu items glow; Got it marks v{NewFeatures.CurrentVersion} as seen and puts the glow out."
            : $"Everything up to v{NewFeatures.CurrentVersion} is acknowledged. The list stays here for reference; the full notes of every release are on GitHub.";
    }

    private void OnGotIt(object? sender, RoutedEventArgs e)
    {
        _acknowledge?.Invoke();
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenReleases(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.RepoUrl + "/releases") { UseShellExecute = true });
        }
        catch { /* browser unavailable */ }
    }
}
