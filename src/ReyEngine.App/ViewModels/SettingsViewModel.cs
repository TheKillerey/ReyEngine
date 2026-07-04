using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Settings;

namespace ReyEngine.App.ViewModels;

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

    [ObservableProperty] private double _mouseLookSensitivity;
    [ObservableProperty] private double _orbitSensitivity;
    [ObservableProperty] private double _panSensitivity;
    [ObservableProperty] private double _zoomSensitivity;
    [ObservableProperty] private bool _invertLookY;
    [ObservableProperty] private double _flySpeed;
    [ObservableProperty] private bool _cullBackfacesDefault;

    public bool Saved { get; private set; }
    public event Action? CloseRequested;

    public SettingsViewModel(EditorSettings src) => LoadFrom(src);

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
        };
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
    private void Cancel() { Saved = false; CloseRequested?.Invoke(); }
}
