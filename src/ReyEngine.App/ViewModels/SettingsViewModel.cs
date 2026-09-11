using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Core.Settings;

namespace ReyEngine.App.ViewModels;

/// <summary>M682: one Blender version in Settings ▸ Blender, with the add-on's state there.</summary>
public sealed partial class BlenderInstallRowViewModel : ObservableObject
{
    private readonly Func<BlenderInstallRowViewModel, Task> _install;

    public BlenderInstallRowViewModel(BlenderAddonInstaller.BlenderInstall install, string? shipped,
        Func<BlenderInstallRowViewModel, Task> installAction)
    {
        Install = install;
        _install = installAction;
        Refresh(shipped);
    }

    public BlenderAddonInstaller.BlenderInstall Install { get; }
    public string Name => "Blender " + Install.Version;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _actionLabel = "Install";
    [ObservableProperty] private bool _busy;

    public void Refresh(string? shipped)
    {
        bool installed = Install.AddonInstalled;
        bool current = Install.AddonCurrent(shipped);
        Detail = (installed ? (current ? "add-on installed, up to date" : "add-on installed, older copy") : "add-on not installed")
                 + (Install.Executable is null ? " · blender.exe not found, enable by hand" : "");
        ActionLabel = installed ? (current ? "Reinstall" : "Update") : "Install";
    }

    [RelayCommand]
    private Task InstallAsync() => _install(this);
}

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

    // M503c: auto-save. Off by default — a mesh move rewrites the whole mapgeo (40 MB on Map453), so this
    // is a quiet-period save, never a per-edit one.
    [ObservableProperty] private bool _autoSaveEdits;
    [ObservableProperty] private int _autoSaveDelaySeconds = 5;

    /// <summary>M681: the update mode, as an index into UpdateService.Modes (0 ask, 1 auto, 2 manual).</summary>
    [ObservableProperty] private int _updateModeIndex;
    /// <summary>What an installer asked for, when one did - shown so a choice the user never made is not
    /// mistaken for one they did.</summary>
    public string UpdateModeHint => UpdateService.InstallerAutoUpdateDefault switch
    {
        true => "The installer set automatic updates as the default for this install.",
        false => "The installer set asking as the default for this install.",
        _ => "The changelog is shown in every mode but manual, which only opens the download page.",
    };
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

    // M72: sidebar section switching (0 General · 1 Camera · 2 Controls · 3 Theme · 4 Preview · 5 Blender)
    [ObservableProperty] private int _selectedSection;
    public bool ShowGeneral => SelectedSection == 0;
    public bool ShowCamera => SelectedSection == 1;
    public bool ShowControls => SelectedSection == 2;
    public bool ShowTheme => SelectedSection == 3;
    public bool ShowPreview => SelectedSection == 4;
    public bool ShowBlender => SelectedSection == 5;   // M682
    public const int BlenderSection = 5;
    partial void OnSelectedSectionChanged(int value)
    {
        OnPropertyChanged(nameof(ShowGeneral));
        OnPropertyChanged(nameof(ShowCamera));
        OnPropertyChanged(nameof(ShowControls));
        OnPropertyChanged(nameof(ShowTheme));
        OnPropertyChanged(nameof(ShowPreview));
        OnPropertyChanged(nameof(ShowBlender));
        if (value == BlenderSection) RefreshBlender();
    }

    // ---- M682: the Blender add-on, installed from here -----------------------------------------------

    public ObservableCollection<BlenderInstallRowViewModel> BlenderInstalls { get; } = new();
    [ObservableProperty] private string _blenderStatus = "";
    /// <summary>The add-on this build ships, or a plain statement that it is missing.</summary>
    public string BlenderAddonSource => BlenderAddonInstaller.ShippedAddonPath() ?? "(the add-on file is missing from this build)";
    public bool HasBlenderInstalls => BlenderInstalls.Count > 0;

    public void RefreshBlender()
    {
        string? shipped = BlenderAddonInstaller.ShippedAddonPath();
        BlenderInstalls.Clear();
        foreach (var install in BlenderAddonInstaller.Discover())
            BlenderInstalls.Add(new BlenderInstallRowViewModel(install, shipped, InstallBlenderAsync));
        OnPropertyChanged(nameof(HasBlenderInstalls));
        OnPropertyChanged(nameof(BlenderAddonSource));
        BlenderStatus = BlenderInstalls.Count == 0
            ? "No Blender found: it creates %AppData%\\Blender Foundation\\Blender\\<version> on its first start."
            : shipped is null ? "The add-on file is missing from this build - reinstall ReyEngine." : "";
    }

    private async Task InstallBlenderAsync(BlenderInstallRowViewModel row)
    {
        string? shipped = BlenderAddonInstaller.ShippedAddonPath();
        if (shipped is null) { BlenderStatus = "The add-on file is missing from this build."; return; }
        row.Busy = true;
        try
        {
            var result = await BlenderAddonInstaller.InstallAsync(row.Install, shipped, enable: true);
            row.Refresh(shipped);
            BlenderStatus = result.Message;
        }
        finally { row.Busy = false; }
    }

    [RelayCommand]
    private async Task InstallBlenderEverywhere()
    {
        foreach (var row in BlenderInstalls.ToList()) await InstallBlenderAsync(row);
        if (BlenderInstalls.Count > 0)
            BlenderStatus = $"Installed for {BlenderInstalls.Count(r => r.Install.AddonInstalled)} of {BlenderInstalls.Count} Blender version(s).";
    }

    // ---- M683: the user's own layer - accent, picture, glass - applied LIVE like the palette ----------

    /// <summary>The accent as the picker holds it; <see cref="AccentIsCustom"/> says whether it is the
    /// user's or the palette's (the picker always shows something).</summary>
    [ObservableProperty] private Avalonia.Media.Color _accentColor = Avalonia.Media.Colors.Transparent;
    [ObservableProperty] private bool _accentIsCustom;
    [ObservableProperty] private string _backgroundImagePath = "";
    [ObservableProperty] private double _backgroundOpacityPercent = 35;
    [ObservableProperty] private double _backgroundGlassPercent = 50;
    [ObservableProperty] private int _backgroundStretchIndex;
    private bool _lookLoading;

    public bool HasBackgroundImage => !string.IsNullOrWhiteSpace(BackgroundImagePath);
    public string BackgroundImageHint => HasBackgroundImage
        ? (System.IO.File.Exists(BackgroundImagePath) ? "png, jpg, bmp, webp - and a gif shows its first frame." : "That file does not exist.")
        : "No picture. Pick one to see the editor through it.";

    partial void OnAccentColorChanged(Avalonia.Media.Color value)
    {
        if (_lookLoading) return;
        AccentIsCustom = true;
        ApplyLook();
    }
    partial void OnBackgroundImagePathChanged(string value)
    { OnPropertyChanged(nameof(HasBackgroundImage)); OnPropertyChanged(nameof(BackgroundImageHint)); if (!_lookLoading) ApplyLook(); }
    partial void OnBackgroundOpacityPercentChanged(double value) { if (!_lookLoading) ApplyLook(); }
    partial void OnBackgroundGlassPercentChanged(double value) { if (!_lookLoading) ApplyLook(); }
    partial void OnBackgroundStretchIndexChanged(int value) { if (!_lookLoading) ApplyLook(); }

    /// <summary>Back to the palette's own accent.</summary>
    [RelayCommand]
    private void ResetAccent()
    {
        AccentIsCustom = false;
        _lookLoading = true;
        try { AccentColor = PaletteAccent(); } finally { _lookLoading = false; }
        ApplyLook();
    }

    [RelayCommand]
    private void ClearBackgroundImage() => BackgroundImagePath = "";

    /// <summary>The settings as the look controls hold them - what Apply and Save read.</summary>
    public EditorSettings LookSettings() => new()
    {
        Theme = _theme,
        ThemeAccent = AccentIsCustom ? ToHex(AccentColor) : "",
        BackgroundImagePath = BackgroundImagePath.Trim(),
        BackgroundImageOpacity = Math.Clamp(BackgroundOpacityPercent / 100.0, 0, 1),
        BackgroundGlass = Math.Clamp(BackgroundGlassPercent / 100.0, 0, 1),
        BackgroundImageStretch = BackgroundStretchIndex,
    };

    private void ApplyLook() => ThemeService.Apply(LookSettings());

    private void LoadLook(EditorSettings s)
    {
        _lookLoading = true;
        try
        {
            AccentIsCustom = Avalonia.Media.Color.TryParse(s.ThemeAccent ?? "", out var accent) && !string.IsNullOrWhiteSpace(s.ThemeAccent);
            AccentColor = AccentIsCustom ? accent : PaletteAccent();
            BackgroundImagePath = s.BackgroundImagePath ?? "";
            BackgroundOpacityPercent = Math.Round(Math.Clamp(s.BackgroundImageOpacity, 0, 1) * 100);
            BackgroundGlassPercent = Math.Round(Math.Clamp(s.BackgroundGlass, 0, 1) * 100);
            BackgroundStretchIndex = Math.Clamp(s.BackgroundImageStretch, 0, 3);
        }
        finally { _lookLoading = false; }
    }

    private Avalonia.Media.Color PaletteAccent() =>
        Avalonia.Media.Color.TryParse(ThemeService.Presets.FirstOrDefault(p => string.Equals(p.Name, _theme, StringComparison.OrdinalIgnoreCase))?.Accent ?? "#E5484D", out var c)
            ? c : Avalonia.Media.Colors.Red;

    public static string ToHex(Avalonia.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

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

    /// <summary>M593: the settings this dialog was opened from. <see cref="ToSettings"/> starts from a
    /// clone of these so a field the dialog does not edit SURVIVES a save.</summary>
    private EditorSettings _source = new();

    private void LoadFrom(EditorSettings s)
    {
        _source = s.Clone();

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
        AutoSaveEdits = s.AutoSaveEdits;                       // M503c
        AutoSaveDelaySeconds = s.EffectiveAutoSaveDelaySeconds;
        UpdateModeIndex = UpdateService.IndexOfMode(UpdateService.EffectiveMode(s.UpdateMode));   // M681
        ProjectsDirectory = s.ProjectsDirectory;
        WwiseConsolePath = s.WwiseConsolePath;
        WwiseProjectPath = s.WwiseProjectPath;
        PreviewBackgroundMapFolder = s.PreviewBackgroundMapFolder;
        PreviewBackgroundEnabled = s.PreviewBackgroundEnabled;
        BuildMapPacks();   // M142.8: downloadable/selectable legacy map packs (reflects the folder above)

        // theme: reflect + live-apply (LoadFrom also runs on Reset to Defaults)
        _theme = s.Theme;
        foreach (var t in Themes) t.IsSelected = string.Equals(t.Name, _theme, StringComparison.OrdinalIgnoreCase);
        LoadLook(s);        // M683: the user's layer, then the whole look at once
        ApplyLook();
    }

    /// <summary>Build an EditorSettings from the current edited state.</summary>
    public EditorSettings ToSettings()
    {
        string K(string id) => Keybinds.First(k => k.ActionId == id).Key;
        // M593: start from what was loaded, then overwrite only what this dialog owns.
        //
        // This used to build a BRAND NEW EditorSettings, so every field the dialog does not show was
        // silently reset by Preferences > Save - FirstRunCompleted among them, which put the first-run
        // wizard back on the next launch. Cloning makes that class of bug structural rather than a thing
        // each new field has to remember, which matters because CopyFrom carries fields ToSettings did not.
        var result = _source.Clone();
        result.CopyFrom(new EditorSettings
        {
            FlyForward = K("FlyForward"), FlyBack = K("FlyBack"), FlyLeft = K("FlyLeft"), FlyRight = K("FlyRight"),
            FlyUp = K("FlyUp"), FlyDown = K("FlyDown"), FocusSelected = K("FocusSelected"),
            MouseLookSensitivity = MouseLookSensitivity, OrbitSensitivity = OrbitSensitivity,
            PanSensitivity = PanSensitivity, ZoomSensitivity = ZoomSensitivity,
            InvertLookY = InvertLookY, FlySpeed = FlySpeed, CullBackfacesDefault = CullBackfacesDefault,
            AutoSaveEdits = AutoSaveEdits, AutoSaveDelaySeconds = AutoSaveDelaySeconds,   // M503c
            UpdateMode = UpdateService.ModeAtIndex(UpdateModeIndex),   // M681
            Theme = _theme,
            ThemeAccent = LookSettings().ThemeAccent, BackgroundImagePath = LookSettings().BackgroundImagePath,   // M683
            BackgroundImageOpacity = LookSettings().BackgroundImageOpacity, BackgroundGlass = LookSettings().BackgroundGlass,
            BackgroundImageStretch = BackgroundStretchIndex,
            PreviewBackgroundMapFolder = PreviewBackgroundMapFolder, PreviewBackgroundEnabled = PreviewBackgroundEnabled,
            ProjectsDirectory = ProjectsDirectory.Trim(),
            WwiseConsolePath = WwiseConsolePath.Trim(), WwiseProjectPath = WwiseProjectPath.Trim(),
            // Carried through unchanged - the dialog does not edit these.
            FirstRunCompleted = _source.FirstRunCompleted,
            LastSeenFeatureVersion = _source.LastSeenFeatureVersion,
        });
        return result;
    }

    /// <summary>M72: pick a theme card — applies immediately so the whole editor previews it.</summary>
    [RelayCommand]
    private void SelectTheme(ThemeItemViewModel? item)
    {
        if (item is null) return;
        foreach (var t in Themes) t.IsSelected = ReferenceEquals(t, item);
        _theme = item.Name;
        if (!AccentIsCustom) { _lookLoading = true; try { AccentColor = PaletteAccent(); } finally { _lookLoading = false; } }   // M683: after _theme, so the picker shows THIS palette
        ApplyLook();   // M683: the palette under the user's layer
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
        ThemeService.Apply(_source);   // M72/M683: revert any live preview - palette, accent, picture, glass
        CloseRequested?.Invoke();
    }
}
