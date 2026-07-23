using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Core.Settings;

namespace ReyEngine.App.ViewModels;

/// <summary>One selectable theme card in Settings ▸ Theme (M72).</summary>
public sealed partial class ThemeItemViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string Tagline { get; init; }
    public required Avalonia.Media.IBrush Accent { get; init; }
    public required Avalonia.Media.IBrush Surface { get; init; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>M142.8: one downloadable legacy (NVR) map pack in Settings ▸ Preview. Each pack can be
/// downloaded on demand and picked as the model-preview backdrop, so the user chooses which classic map
/// their skins are previewed in (Crystal Scar, Twisted Treeline, …).</summary>
public sealed partial class MapPackRowViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string InstallDir { get; init; }
    public required string Url { get; init; }

    [ObservableProperty] private bool _installed;
    [ObservableProperty] private bool _isSelected;   // currently the preview backdrop
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;

    /// <summary>Host hooks (wired by SettingsViewModel): use this pack as the backdrop / global busy gate.</summary>
    public Action<MapPackRowViewModel>? UseAsBackdrop;
    public Func<bool>? AnyBusy;

    public string ActionLabel => Installed ? "Use as backdrop" : "⬇ Download";
    partial void OnInstalledChanged(bool value) => OnPropertyChanged(nameof(ActionLabel));

    [RelayCommand]
    private async System.Threading.Tasks.Task Activate()
    {
        if (Busy || (AnyBusy?.Invoke() ?? false)) return;
        try
        {
            if (!Installed)
            {
                Busy = true;
                var progress = new Progress<string>(s => Status = s);
                await SetupService.DownloadAndExtractAsync(Url, InstallDir, progress);
                Installed = true;
            }
            UseAsBackdrop?.Invoke(this);
            Status = "Selected as the preview backdrop. Save to apply.";
        }
        catch (Exception ex) { Status = $"Download failed: {ex.Message}"; }
        finally { Busy = false; }
    }
}

/// <summary>One rebindable action row in the Settings ▸ Controls tab (M40).</summary>
public sealed partial class KeybindRowViewModel : ObservableObject
{
    public required string Label { get; init; }
    public required string ActionId { get; init; }   // logical id -> EditorSettings field
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private bool _isCapturing;

    public string Display => IsCapturing ? "Press a key…  (Esc cancels)" : (string.IsNullOrEmpty(Key) ? "—" : Key);
    partial void OnKeyChanged(string value) => OnPropertyChanged(nameof(Display));
    partial void OnIsCapturingChanged(bool value) => OnPropertyChanged(nameof(Display));
}

/// <summary>
/// View-model for the Settings window (M40): edit a copy of <see cref="EditorSettings"/> — viewport keybinds
/// and camera feel — then Save (persist + apply) or Cancel. Mirrors ProjectSettingsViewModel's
/// CloseRequested/Saved handshake so the host window stays view-only.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public ObservableCollection<KeybindRowViewModel> Keybinds { get; } = new();
    public ObservableCollection<ThemeItemViewModel> Themes { get; } = new();

    [ObservableProperty] private double _mouseLookSensitivity;
    [ObservableProperty] private double _orbitSensitivity;
    [ObservableProperty] private double _panSensitivity;
    [ObservableProperty] private double _zoomSensitivity;
    [ObservableProperty] private bool _invertLookY;
    [ObservableProperty] private double _flySpeed;
    [ObservableProperty] private bool _cullBackfacesDefault;
    [ObservableProperty] private string _projectsDirectory = "";   // M133
    [ObservableProperty] private string _wwiseConsolePath = "";    // M138
    [ObservableProperty] private string _wwiseProjectPath = "";

    // M88: character-preview NVR map backdrop
    [ObservableProperty] private string _previewBackgroundMapFolder = "";
    [ObservableProperty] private bool _previewBackgroundEnabled;

    // M92/M93 → M142.8: legacy map packs are a LIST now (Crystal Scar, Twisted Treeline, …) — each can be
    // downloaded on demand and picked as the preview backdrop, instead of a hardcoded Map8-only button.
    public ObservableCollection<MapPackRowViewModel> MapPacks { get; } = new();

    /// <summary>Build the pack rows from SetupService and wire their host hooks. Marks the row whose
    /// install dir is the current backdrop folder as selected.</summary>
    private void BuildMapPacks()
    {
        MapPacks.Clear();
        foreach (var (name, dir, url, installed) in SetupService.LegacyMapPacks)
        {
            var row = new MapPackRowViewModel
            {
                Name = name, InstallDir = dir, Url = url, Installed = installed,
                IsSelected = string.Equals(PreviewBackgroundMapFolder, dir, StringComparison.OrdinalIgnoreCase),
            };
            row.AnyBusy = () => MapPacks.Any(p => p.Busy);
            row.UseAsBackdrop = r =>
            {
                PreviewBackgroundMapFolder = r.InstallDir;
                PreviewBackgroundEnabled = true;
                foreach (var p in MapPacks) p.IsSelected = ReferenceEquals(p, r);
            };
            MapPacks.Add(row);
        }
    }

    partial void OnPreviewBackgroundMapFolderChanged(string value)
    {
        foreach (var p in MapPacks)
            p.IsSelected = string.Equals(value, p.InstallDir, StringComparison.OrdinalIgnoreCase);
    }

    // M72: sidebar section switching (0 General · 1 Camera · 2 Controls · 3 Theme · 4 Preview)
    [ObservableProperty] private int _selectedSection;
    public bool ShowGeneral => SelectedSection == 0;
    public bool ShowCamera => SelectedSection == 1;
    public bool ShowControls => SelectedSection == 2;
    public bool ShowTheme => SelectedSection == 3;
    public bool ShowPreview => SelectedSection == 4;
    partial void OnSelectedSectionChanged(int value)
    {
        OnPropertyChanged(nameof(ShowGeneral));
        OnPropertyChanged(nameof(ShowCamera));
        OnPropertyChanged(nameof(ShowControls));
        OnPropertyChanged(nameof(ShowTheme));
        OnPropertyChanged(nameof(ShowPreview));
    }

    // M72: theme choice — applied LIVE while browsing so the user sees it; Cancel reverts.
    private string _theme = ThemeService.DefaultTheme;
    private readonly string _originalTheme;

    public bool Saved { get; private set; }
    public event Action? CloseRequested;

    public SettingsViewModel(EditorSettings src)
    {
        _originalTheme = src.Theme;
        foreach (var p in ThemeService.Presets)
            Themes.Add(new ThemeItemViewModel
            {
                Name = p.Name,
                Tagline = p.Tagline,
                Accent = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(p.Accent)),
                Surface = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(p.Surface)),
            });
        LoadFrom(src);
    }

    private void LoadFrom(EditorSettings s)
    {
        Keybinds.Clear();
        Keybinds.Add(new KeybindRowViewModel { Label = "Fly Forward", ActionId = "FlyForward", Key = s.FlyForward });
        Keybinds.Add(new KeybindRowViewModel { Label = "Fly Back", ActionId = "FlyBack", Key = s.FlyBack });
        Keybinds.Add(new KeybindRowViewModel { Label = "Fly Left", ActionId = "FlyLeft", Key = s.FlyLeft });
        Keybinds.Add(new KeybindRowViewModel { Label = "Fly Right", ActionId = "FlyRight", Key = s.FlyRight });
        Keybinds.Add(new KeybindRowViewModel { Label = "Fly Up", ActionId = "FlyUp", Key = s.FlyUp });
        Keybinds.Add(new KeybindRowViewModel { Label = "Fly Down", ActionId = "FlyDown", Key = s.FlyDown });
        Keybinds.Add(new KeybindRowViewModel { Label = "Focus Selected", ActionId = "FocusSelected", Key = s.FocusSelected });

        MouseLookSensitivity = s.MouseLookSensitivity;
        OrbitSensitivity = s.OrbitSensitivity;
        PanSensitivity = s.PanSensitivity;
        ZoomSensitivity = s.ZoomSensitivity;
        InvertLookY = s.InvertLookY;
        FlySpeed = s.FlySpeed;
        CullBackfacesDefault = s.CullBackfacesDefault;
        ProjectsDirectory = s.ProjectsDirectory;
        WwiseConsolePath = s.WwiseConsolePath;
        WwiseProjectPath = s.WwiseProjectPath;
        PreviewBackgroundMapFolder = s.PreviewBackgroundMapFolder;
        PreviewBackgroundEnabled = s.PreviewBackgroundEnabled;
        BuildMapPacks();   // M142.8: downloadable/selectable legacy map packs (reflects the folder above)

        // theme: reflect + live-apply (LoadFrom also runs on Reset to Defaults)
        _theme = s.Theme;
        foreach (var t in Themes) t.IsSelected = string.Equals(t.Name, _theme, StringComparison.OrdinalIgnoreCase);
        ThemeService.Apply(_theme);
    }

    /// <summary>Build an EditorSettings from the current edited state.</summary>
    public EditorSettings ToSettings()
    {
        string K(string id) => Keybinds.First(k => k.ActionId == id).Key;
        return new EditorSettings
        {
            FlyForward = K("FlyForward"), FlyBack = K("FlyBack"), FlyLeft = K("FlyLeft"), FlyRight = K("FlyRight"),
            FlyUp = K("FlyUp"), FlyDown = K("FlyDown"), FocusSelected = K("FocusSelected"),
            MouseLookSensitivity = MouseLookSensitivity, OrbitSensitivity = OrbitSensitivity,
            PanSensitivity = PanSensitivity, ZoomSensitivity = ZoomSensitivity,
            InvertLookY = InvertLookY, FlySpeed = FlySpeed, CullBackfacesDefault = CullBackfacesDefault,
            Theme = _theme,
            PreviewBackgroundMapFolder = PreviewBackgroundMapFolder, PreviewBackgroundEnabled = PreviewBackgroundEnabled,
            ProjectsDirectory = ProjectsDirectory.Trim(),
            WwiseConsolePath = WwiseConsolePath.Trim(), WwiseProjectPath = WwiseProjectPath.Trim(),
        };
    }

    /// <summary>M72: pick a theme card — applies immediately so the whole editor previews it.</summary>
    [RelayCommand]
    private void SelectTheme(ThemeItemViewModel? item)
    {
        if (item is null) return;
        foreach (var t in Themes) t.IsSelected = ReferenceEquals(t, item);
        _theme = item.Name;
        ThemeService.Apply(_theme);
    }

    /// <summary>Begin capturing a new key for a row (cancels any other in-progress capture).</summary>
    [RelayCommand]
    private void StartCapture(KeybindRowViewModel? row)
    {
        if (row is null) return;
        foreach (var k in Keybinds) k.IsCapturing = false;
        row.IsCapturing = true;
    }

    /// <summary>Assign the captured key to whichever row is listening (called from the window's KeyDown).</summary>
    public bool AssignCapturedKey(string keyName)
    {
        var row = Keybinds.FirstOrDefault(k => k.IsCapturing);
        if (row is null) return false;
        row.Key = keyName;
        row.IsCapturing = false;
        return true;
    }

    public void CancelCapture()
    {
        foreach (var k in Keybinds) k.IsCapturing = false;
    }

    [RelayCommand]
    private void ResetDefaults() => LoadFrom(new EditorSettings());

    [RelayCommand]
    private void Save() { Saved = true; CloseRequested?.Invoke(); }

    [RelayCommand]
    private void Cancel()
    {
        Saved = false;
        ThemeService.Apply(_originalTheme);   // M72: revert any live theme preview
        CloseRequested?.Invoke();
    }
}
