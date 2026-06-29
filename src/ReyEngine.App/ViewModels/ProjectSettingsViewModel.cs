using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Core.Projects;

namespace ReyEngine.App.ViewModels;

/// <summary>Backs the Project Settings dialog: .fantome mod metadata + game/output folders.</summary>
public sealed partial class ProjectSettingsViewModel : ViewModelBase
{
    private readonly DialogService _dialogs;

    [ObservableProperty] private string _modName = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _version = "1.0.0";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _heart = "";
    [ObservableProperty] private string _home = "";
    [ObservableProperty] private string _thumbnailPath = "";
    [ObservableProperty] private string _gameDirectory = "";
    [ObservableProperty] private string _outputDirectory = "";

    public bool Saved { get; private set; }
    public event Action? CloseRequested;

    public ProjectSettingsViewModel(ReyProject p, DialogService dialogs)
    {
        _dialogs = dialogs;
        _modName = p.EffectiveModName;
        _author = p.ModAuthor ?? "";
        _version = string.IsNullOrWhiteSpace(p.ModVersion) ? "1.0.0" : p.ModVersion;
        _description = p.ModDescription ?? "";
        _heart = p.ModHeart ?? "";
        _home = p.ModHome ?? "";
        _thumbnailPath = p.ThumbnailPath ?? "";
        _gameDirectory = p.GameDirectory ?? "";
        _outputDirectory = p.OutputDirectory ?? "";
    }

    public void ApplyTo(ReyProject p)
    {
        p.ModName = string.IsNullOrWhiteSpace(ModName) ? null : ModName.Trim();
        p.ModAuthor = string.IsNullOrWhiteSpace(Author) ? null : Author.Trim();
        p.ModVersion = string.IsNullOrWhiteSpace(Version) ? "1.0.0" : Version.Trim();
        p.ModDescription = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        p.ModHeart = string.IsNullOrWhiteSpace(Heart) ? null : Heart.Trim();
        p.ModHome = string.IsNullOrWhiteSpace(Home) ? null : Home.Trim();
        p.ThumbnailPath = string.IsNullOrWhiteSpace(ThumbnailPath) ? null : ThumbnailPath.Trim();
        if (!string.IsNullOrWhiteSpace(GameDirectory)) p.GameDirectory = GameDirectory.Trim();
        if (!string.IsNullOrWhiteSpace(OutputDirectory)) p.OutputDirectory = OutputDirectory.Trim();
    }

    [RelayCommand]
    private async Task BrowseThumbnail()
    {
        var img = new FilePickerFileType("Image") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg" } };
        var f = await _dialogs.OpenFileAsync("Select a thumbnail image", img, DialogService.All);
        if (f is not null) ThumbnailPath = f;
    }

    [RelayCommand]
    private async Task BrowseGame()
    {
        var f = await _dialogs.OpenFolderAsync("Select the League of Legends 'Game' folder");
        if (f is not null) GameDirectory = f;
    }

    [RelayCommand]
    private async Task BrowseOutput()
    {
        var f = await _dialogs.OpenFolderAsync("Select the build output folder");
        if (f is not null) OutputDirectory = f;
    }

    [RelayCommand]
    private void Save()
    {
        Saved = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
