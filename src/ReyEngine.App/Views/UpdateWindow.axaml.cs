using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ReyEngine.App.Services;

namespace ReyEngine.App.Views;

/// <summary>M681: the update dialog. Shows the release notes and offers the update three ways; in the
/// automatic mode the download starts as the window opens and the install follows when it is ready.</summary>
public partial class UpdateWindow : Window
{
    private UpdateService.UpdateCheck _check = new(false, false, null, null, null);
    private Action<string>? _persistMode;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public UpdateWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape && !_busy) { Close(); e.Handled = true; } };
        Closing += (_, _) => _cts?.Cancel();
    }

    /// <summary>Show the dialog for a check that found a newer release. <paramref name="mode"/> is the
    /// effective update mode; <paramref name="persistMode"/> is called with "auto" or "ask" when the user
    /// flips the checkbox, so the choice outlives the dialog.</summary>
    public static async Task ShowAsync(Window owner, UpdateService.UpdateCheck check, string mode, Action<string> persistMode)
    {
        var win = new UpdateWindow { _check = check, _persistMode = persistMode };
        win.HeadlineText.Text = string.IsNullOrWhiteSpace(check.ReleaseName) ? $"ReyEngine {check.LatestVersion}" : check.ReleaseName;
        win.VersionsText.Text = $"{check.LatestVersion} is available — you have {AppInfo.DisplayVersion}.";
        win.ChangelogText.Text = UpdateService.ChangelogToText(check.Body);
        win.AutoBox.IsChecked = mode == UpdateService.ModeAuto;
        win.AutoBox.IsCheckedChanged += (_, _) => persistMode(win.AutoBox.IsChecked == true ? UpdateService.ModeAuto : UpdateService.ModeAsk);
        if (UpdateApplier.PickAsset(check.Assets, UpdateApplier.CurrentInstallKind) is null)
        {
            win.UpdateButton.IsEnabled = false;
            win.UpdateButton.Content = "No build to install";
        }
        else if (mode == UpdateService.ModeAuto)
            win.Opened += (_, _) => _ = win.RunUpdateAsync();
        await win.ShowDialog(owner);
    }

    private void OnOpenPage(object? sender, RoutedEventArgs e) => UpdateService.OpenReleasePage(_check);
    private void OnLater(object? sender, RoutedEventArgs e) { if (!_busy) Close(); else _cts?.Cancel(); }
    private void OnUpdate(object? sender, RoutedEventArgs e) => _ = RunUpdateAsync();

    private async Task RunUpdateAsync()
    {
        if (_busy) return;
        _busy = true;
        _cts = new CancellationTokenSource();
        UpdateButton.IsEnabled = false;
        LaterButton.Content = "Cancel";
        ErrorText.IsVisible = false;
        ProgressPanel.IsVisible = true;
        var progress = new Progress<(double Fraction, string Status)>(p => Dispatcher.UIThread.Post(() =>
        {
            Progress.Value = p.Fraction;
            StatusText.Text = p.Status;
        }));
        try
        {
            var prepared = await UpdateApplier.PrepareAsync(_check, progress, _cts.Token);
            StatusText.Text = prepared.Summary + " Restarting…";
            await Task.Delay(1200, _cts.Token);
            UpdateApplier.ApplyAndExit(prepared);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
            RestoreButtons();
        }
        catch (Exception ex)
        {
            ErrorText.Text = "The update could not be installed: " + ex.Message
                             + Environment.NewLine + "The download page still works.";
            ErrorText.IsVisible = true;
            RestoreButtons();
        }
    }

    private void RestoreButtons()
    {
        _busy = false;
        UpdateButton.IsEnabled = true;
        LaterButton.Content = "Later";
    }
}
