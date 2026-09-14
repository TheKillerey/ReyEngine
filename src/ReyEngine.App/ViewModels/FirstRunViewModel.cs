using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Core.Projects;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M93: first-run setup wizard — one checklist that gets a fresh install fully working: verify the
/// League install, download CommunityDragon hashes, the vgmstream audio decoder, and the optional
/// Dominion preview map. Each step is skippable and re-runnable later (Help ▸ Setup Wizard).
/// </summary>
public sealed partial class FirstRunViewModel : ObservableObject
{
    private readonly MainWindowViewModel _main;
    public event Action? CloseRequested;

    // ---- game install ----
    [ObservableProperty] private string _gameStatus = "";
    [ObservableProperty] private bool _gameOk;

    // ---- hashes ----
    [ObservableProperty] private string _hashStatus = "";
    [ObservableProperty] private bool _hashOk;
    [ObservableProperty] private bool _hashBusy;

    // ---- vgmstream ----
    [ObservableProperty] private string _audioStatus = "";
    [ObservableProperty] private bool _audioOk;
    [ObservableProperty] private bool _audioBusy;

    // ---- Dominion map ----
    [ObservableProperty] private string _mapStatus = "";
    [ObservableProperty] private bool _mapOk;
    [ObservableProperty] private bool _mapBusy;

    public FirstRunViewModel(MainWindowViewModel main)
    {
        _main = main;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var installs = GameInstallLocator.Discover();
        GameOk = installs.Count > 0;
        GameStatus = GameOk
            ? string.Join("\n", installs.Select(i => $"✔ {i.Display}"))
            : "No League of Legends install found. Install LIVE or PBE, or browse for it later in the New Project wizard.";

        HashOk = SetupService.HashesInstalled;
        HashStatus = HashOk
            ? "✔ Hash tables installed — file names resolve."
            : "Required for readable file names (~100 MB from CommunityDragon).";

        AudioOk = _main.Sound.IsAvailable || SetupService.VgmstreamInstalled;
        AudioStatus = AudioOk
            ? $"✔ vgmstream found — audio playback available."
            : "Needed to play Wwise audio (SFX preview, map ambience). ~2 MB, ISC-licensed.";

        // M725: BOTH preview environments, not just Dominion. Twisted Treeline was downloadable in
        // Settings and invisible here, so a first run left the user believing Dominion was the only one.
        bool dominion = SetupService.Map8Installed, treeline = SetupService.Map10Installed;
        MapOk = dominion || treeline;
        MapStatus = (dominion, treeline) switch
        {
            (true, true) => "✔ Dominion and Twisted Treeline installed — both available as Model Preview backdrops.",
            (true, false) => "✔ Dominion installed. Twisted Treeline is also available (Settings ▸ Maps).",
            (false, true) => "✔ Twisted Treeline installed. Dominion is also available (Settings ▸ Maps).",
            _ => "Optional: a classic map as a 3D backdrop behind previewed champions — Dominion or Twisted Treeline (~66 MB each).",
        };
    }

    [RelayCommand]
    private async Task DownloadHashes()
    {
        if (HashBusy) return;
        HashBusy = true;
        try
        {
            HashStatus = "Syncing CommunityDragon hashes… (see console for details)";
            await _main.SyncHashesCommand.ExecuteAsync(null);
            RefreshStatus();
        }
        catch (Exception ex) { HashStatus = $"Failed: {ex.Message}"; }
        finally { HashBusy = false; }
    }

    [RelayCommand]
    private async Task DownloadVgmstream()
    {
        if (AudioBusy) return;
        AudioBusy = true;
        try
        {
            var progress = new Progress<string>(s => AudioStatus = s);
            await SetupService.DownloadAndExtractAsync(SetupService.VgmstreamUrl, SetupService.VgmstreamDir, progress);
            if (SetupService.VgmstreamInstalled)
            {
                _main.Sound.VgmstreamPath = SetupService.VgmstreamExe;
                RefreshStatus();
            }
            else AudioStatus = "Extracted, but vgmstream-cli.exe was not found in the package.";
        }
        catch (Exception ex) { AudioStatus = $"Failed: {ex.Message}"; }
        finally { AudioBusy = false; }
    }

    [RelayCommand]
    /// <summary>M725: either preview environment. <paramref name="pack"/> is "Map10" for Twisted Treeline
    /// and anything else for Dominion, which keeps the old no-argument binding working as Dominion.</summary>
    private async Task DownloadMap(string? pack)
    {
        if (MapBusy) return;
        MapBusy = true;
        try
        {
            bool treeline = string.Equals(pack, "Map10", StringComparison.OrdinalIgnoreCase);
            string url = treeline ? SetupService.Map10Url : SetupService.Map8Url;
            string dir = treeline ? SetupService.Map10InstallDir : SetupService.Map8InstallDir;

            var progress = new Progress<string>(s => MapStatus = s);
            await SetupService.DownloadAndExtractAsync(url, dir, progress);
            if (SetupService.HasNvr(dir))
            {
                _main.Settings.PreviewBackgroundMapFolder = dir;
                _main.Settings.PreviewBackgroundEnabled = true;
                _main.Settings.Save();
                // M725: the loaded backdrop is cached by folder; a fresh download must not serve a stale one
                _main.InvalidatePreviewBackground();
                RefreshStatus();
            }
            else MapStatus = "Extracted, but Scene\\room.nvr was not found in the package.";
        }
        catch (Exception ex) { MapStatus = $"Failed: {ex.Message}"; }
        finally { MapBusy = false; }
    }

    [RelayCommand]
    private void Finish()
    {
        _main.Settings.FirstRunCompleted = true;
        _main.Settings.Save();
        CloseRequested?.Invoke();
    }
}
