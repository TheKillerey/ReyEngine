using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using ReyEngine.App.Services;

namespace ReyEngine.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = AppInfo.DisplayVersion;
        AuthorText.Text = $"by {AppInfo.Author}";
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "reyengine_logo.png");
            if (File.Exists(path)) LogoImage.Source = new Bitmap(path);
        }
        catch { /* cosmetic */ }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenRepo(object? sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.RepoUrl) { UseShellExecute = true });
        }
        catch { /* browser unavailable */ }
    }

    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        UpdateStatus.IsVisible = true;
        UpdateStatus.Text = "Checking…";
        var r = await UpdateService.CheckAsync();
        if (!r.Success)
            UpdateStatus.Text = $"Could not check for updates ({r.Error}). If no release is published yet, this is expected.";
        else if (r.UpdateAvailable)
        {
            // M681: the changelog and the install live in the update dialog; this line only reports.
            UpdateStatus.Text = $"Update available: {r.LatestVersion} (you have {AppInfo.DisplayVersion}).";
            var settings = ReyEngine.Core.Settings.EditorSettings.Load();
            await UpdateWindow.ShowAsync(this, r, UpdateService.EffectiveMode(settings.UpdateMode), chosen =>
            {
                settings.UpdateMode = chosen;
                try { settings.Save(); } catch { }
            });
        }
        else UpdateStatus.Text = $"You're up to date ({AppInfo.DisplayVersion}).";
    }
}
