using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReyEngine.Core.Settings;

/// <summary>
/// User-configurable editor preferences (M40): viewport keybinds + camera feel, persisted to
/// <c>%AppData%/ReyEngine/settings.json</c>. Backend-agnostic POCO — the App binds a Settings window to it,
/// applies it to the viewport, and calls <see cref="Save"/>. Never throws on load; falls back to defaults.
/// </summary>
public sealed class EditorSettings
{
    // ---- viewport fly / focus keybinds (Avalonia Key enum names, e.g. "W", "Space") ----
    public string FlyForward { get; set; } = "W";
    public string FlyBack { get; set; } = "S";
    public string FlyLeft { get; set; } = "A";
    public string FlyRight { get; set; } = "D";
    public string FlyUp { get; set; } = "E";
    public string FlyDown { get; set; } = "Q";
    public string FocusSelected { get; set; } = "F";

    // ---- camera feel (multipliers, 1.0 = default) ----
    public double MouseLookSensitivity { get; set; } = 1.0;
    public double OrbitSensitivity { get; set; } = 1.0;
    public double PanSensitivity { get; set; } = 1.0;
    public double ZoomSensitivity { get; set; } = 1.0;
    public bool InvertLookY { get; set; } = false;

    /// <summary>Base WASD fly speed in world units/sec (wheel still scales it live within a session).</summary>
    public double FlySpeed { get; set; } = 600.0;

    // ---- viewport defaults ----
    public bool CullBackfacesDefault { get; set; } = true;

    // ---- appearance (M72) ----
    /// <summary>UI theme palette name (Themes/Palettes/*.axaml). Unknown names fall back to the default.</summary>
    public string Theme { get; set; } = "Crimson";

    // ---- character-preview backdrop (M88) ----
    /// <summary>Path to a legacy League LEVELS/&lt;Map&gt; folder (containing Scene/room.nvr) used as the
    /// 3D backdrop behind previewed characters. Empty disables the feature.</summary>
    public string PreviewBackgroundMapFolder { get; set; } = "";
    /// <summary>Whether to render the NVR map backdrop in the model preview window.</summary>
    public bool PreviewBackgroundEnabled { get; set; } = false;

    [JsonIgnore]
    public static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReyEngine", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static EditorSettings Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(StorePath)) ?? new();
        }
        catch { /* corrupt / unreadable — fall back to defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* best-effort */ }
    }

    /// <summary>Deep copy (for edit-then-cancel in the Settings window).</summary>
    public EditorSettings Clone() => (EditorSettings)MemberwiseClone();

    /// <summary>Overwrite this instance's values from another (apply an edited copy in place).</summary>
    public void CopyFrom(EditorSettings s)
    {
        FlyForward = s.FlyForward; FlyBack = s.FlyBack; FlyLeft = s.FlyLeft; FlyRight = s.FlyRight;
        FlyUp = s.FlyUp; FlyDown = s.FlyDown; FocusSelected = s.FocusSelected;
        MouseLookSensitivity = s.MouseLookSensitivity; OrbitSensitivity = s.OrbitSensitivity;
        PanSensitivity = s.PanSensitivity; ZoomSensitivity = s.ZoomSensitivity;
        InvertLookY = s.InvertLookY; FlySpeed = s.FlySpeed; CullBackfacesDefault = s.CullBackfacesDefault;
        Theme = s.Theme;
        PreviewBackgroundMapFolder = s.PreviewBackgroundMapFolder; PreviewBackgroundEnabled = s.PreviewBackgroundEnabled;
    }
}
