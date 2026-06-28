using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Imaging;
using ReyEngine.App.Services;
using ReyEngine.Core;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Diagnostics;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;

namespace ReyEngine.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly Logger _log = new();
    private readonly HashDictionary _hashes = new();
    private WadArchive? _archive;

    public DialogService Dialogs { get; } = new();
    public ConsoleViewModel Console { get; } = new();
    public InspectorViewModel Inspector { get; } = new();
    public ReyProject Project { get; } = new();
    public ObservableCollection<AssetNodeViewModel> RootNodes { get; } = new();

    [ObservableProperty] private AssetNodeViewModel? _selectedNode;
    [ObservableProperty] private string _title = "ReyEngine";
    [ObservableProperty] private string _status = "Ready  —  open a .wad.client to begin";
    [ObservableProperty] private string _hashInput = "";

    public MainWindowViewModel()
    {
        _log.AddSink(Console);
        Project.GameDirectory = ReyProject.GuessGameDirectory();

        _log.Info("ReyEngine", "Editor started.");
        if (!string.IsNullOrEmpty(Project.GameDirectory))
            _log.Info("Project", $"Game directory: {Project.GameDirectory}");
        else
            _log.Warn("Project", "League install not auto-detected. Use File ▸ Open WAD.");

        TryLoadDefaultHashes();
    }

    private void TryLoadDefaultHashes()
    {
        var dir = Project.DefaultHashDirectory;
        if (dir is null || !Directory.Exists(dir)) return;
        var (wad, bin) = _hashes.LoadDirectory(dir);
        if (wad + bin > 0)
            _log.Success("Hashes", $"Loaded {wad:n0} WAD paths and {bin:n0} bin names.");
    }

    [RelayCommand]
    private async Task OpenWad()
    {
        var path = await Dialogs.OpenFileAsync("Open WAD archive", DialogService.Wad, DialogService.All);
        if (path is not null) LoadWad(path);
    }

    public void LoadWad(string path)
    {
        try
        {
            _log.Info("WAD", $"Opening {Path.GetFileName(path)} …");
            _archive?.Dispose();
            _archive = WadArchive.Open(path, _hashes);

            var root = AssetTree.Build(_archive.Entries, _archive.Name);
            RootNodes.Clear();
            RootNodes.Add(new AssetNodeViewModel(root));
            Inspector.Clear();

            _log.Success("WAD", $"Loaded {_archive.Entries.Count:n0} chunks ({_archive.ResolvedCount:n0} resolved by hash dictionary).");
            Status = $"{_archive.Name}  —  {_archive.Entries.Count:n0} entries · {_archive.ResolvedCount:n0} resolved";
            Title = $"ReyEngine  —  {_archive.Name}";
        }
        catch (Exception ex)
        {
            _log.Error("WAD", ex.Message);
        }
    }

    [RelayCommand]
    private void ReloadWad()
    {
        if (_archive is null) { _log.Warn("WAD", "No archive is open."); return; }
        LoadWad(_archive.FilePath);
    }

    [RelayCommand]
    private async Task LoadHashes()
    {
        var dir = await Dialogs.OpenFolderAsync("Select hash dictionary folder");
        if (dir is null) return;
        var (wad, bin) = _hashes.LoadDirectory(dir);
        Project.HashDirectory = dir;
        _log.Success("Hashes", $"Loaded {wad:n0} WAD paths and {bin:n0} bin names.");
        if (_archive is not null) LoadWad(_archive.FilePath); // re-resolve with new dictionary
    }

    [RelayCommand]
    private async Task ExportSelected()
    {
        var entry = SelectedNode?.Entry;
        if (entry is null || _archive is null) { _log.Warn("Export", "Select a file in the browser first."); return; }

        var outPath = await Dialogs.SaveFileAsync("Export asset", entry.DisplayName);
        if (outPath is null) return;

        try
        {
            _archive.ExtractToFile(entry, outPath);
            _log.Success("Export", $"Wrote {outPath}");
        }
        catch (Exception ex)
        {
            _log.Error("Export", ex.Message);
        }
    }

    [RelayCommand]
    private void HashLookup()
    {
        if (string.IsNullOrWhiteSpace(HashInput)) { _log.Warn("Hash", "Type a path/string in the toolbar box."); return; }
        var s = HashInput.Trim();
        _log.Info("Hash", $"\"{s}\"");
        _log.Info("Hash", $"   xxhash64 (wad path) = 0x{HashAlgorithms.WadPath(s):x16}");
        _log.Info("Hash", $"   fnv1a    (bin name) = 0x{HashAlgorithms.Fnv1a(s):x8}");
        _log.Info("Hash", $"   elf                 = 0x{HashAlgorithms.Elf(s):x8}");
    }

    [RelayCommand] private void BuildPackage() => _log.Warn("Package", "WAD repack / Build Package lands in milestone M5.");
    [RelayCommand] private void ShaderPreview() => _log.Warn("Shader", "Shader/material preview lands in milestone M4.");
    [RelayCommand] private void ClearConsole() => Console.Clear();

    [RelayCommand]
    private void Exit() =>
        (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    partial void OnSelectedNodeChanged(AssetNodeViewModel? value)
    {
        var entry = value?.Entry;
        if (entry is null) return;
        Inspector.ShowEntry(entry);
        Inspector.SetPreview(null);
        _ = TryPreviewAsync(entry);
    }

    private async Task TryPreviewAsync(WadAssetEntry entry)
    {
        if (_archive is null) return;
        if (entry.Type is not (AssetType.Texture or AssetType.Dds)) return;

        try
        {
            var img = await Task.Run(() =>
            {
                var bytes = _archive.Extract(entry);
                return TextureDecoder.Decode(bytes);
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Inspector.SetPreview(BitmapFactory.FromRgba(img));
                _log.Info("Preview", $"Decoded {entry.DisplayName}  ({img.Width}×{img.Height}).");
            });
        }
        catch (Exception ex)
        {
            _log.Error("Preview", $"{entry.DisplayName}: {ex.Message}");
        }
    }
}
