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

    /// <summary>M762: the viewport renderer. Direct3D 11 is the default; OpenGL only when D3D11 is
    /// unavailable or the user asks.</summary>
    public bool UseOpenGlViewport { get; set; } = false;

    // ---- appearance (M72) ----
    /// <summary>UI theme palette name (Themes/Palettes/*.axaml). Unknown names fall back to the default.</summary>
    public string Theme { get; set; } = "Crimson";

    // ---- appearance, the user's own (M683) ----
    /// <summary>An accent colour of the user's own, as #RRGGBB, laid over the palette's; empty keeps the
    /// palette's accent.</summary>
    public string ThemeAccent { get; set; } = "";
    /// <summary>A picture behind the main window - png, jpg, gif (first frame), bmp or webp. Empty = none.</summary>
    public string BackgroundImagePath { get; set; } = "";
    /// <summary>How strongly the picture shows through, 0..1.</summary>
    public double BackgroundImageOpacity { get; set; } = 0.35;
    /// <summary>How see-through the panels over the picture become, 0 (solid, as without a picture) .. 1.</summary>
    public double BackgroundGlass { get; set; } = 0.5;
    /// <summary>How the picture fills the window: 0 cover (uniform to fill), 1 fit (uniform), 2 stretch, 3 tile.</summary>
    public int BackgroundImageStretch { get; set; } = 0;

    // ---- updates (M681) ----
    /// <summary>How a newer release is handled when one is found: <c>ask</c> shows the changelog and asks,
    /// <c>auto</c> downloads and installs it, <c>manual</c> opens the download page (the pre-M681 way).
    /// Empty means the user never chose, and the effective mode then comes from the installer's default
    /// (an MSI can set it) or falls back to <c>ask</c> - see UpdateService.EffectiveMode.</summary>
    public string UpdateMode { get; set; } = "";

    /// <summary>M731: at startup, fetch a newer Mimir hash-table release (or changed CommunityDragon lists) and
    /// a newer meta-class database when one was published, then re-resolve whatever is open. On by default;
    /// the Settings window's UPDATES card turns it off.</summary>
    public bool AutoUpdateHashes { get; set; } = true;

    // ---- character-preview backdrop (M88) ----
    /// <summary>Path to a legacy League LEVELS/&lt;Map&gt; folder (containing Scene/room.nvr) used as the
    /// 3D backdrop behind previewed characters. Empty disables the feature.</summary>
    public string PreviewBackgroundMapFolder { get; set; } = "";
    /// <summary>Whether to render the NVR map backdrop in the model preview window.</summary>
    public bool PreviewBackgroundEnabled { get; set; } = false;

    // ---- first-run setup (M93) ----
    /// <summary>True once the first-run setup wizard has been completed (or skipped).</summary>
    public bool FirstRunCompleted { get; set; } = false;

    // ---- feature discovery (M593) ----
    /// <summary>
    /// The newest release whose new-feature highlights this user has already seen, e.g. <c>0.4.0</c>.
    ///
    /// <para>Empty means "never seen any", which is what a fresh install and every pre-M593 settings file
    /// both look like — so an existing user updating INTO the release sees that release's highlights once,
    /// which is the point. Deliberately a plain version string rather than a set of feature ids: a string
    /// round-trips through the settings test's supported types, and one comparison answers the question for
    /// every feature at once.</para>
    /// </summary>
    public string LastSeenFeatureVersion { get; set; } = "";

    // ---- auto-save (M503c) ----
    /// <summary>
    /// Save mesh transforms and material edits automatically once editing goes quiet.
    ///
    /// <para>OFF by default, deliberately. Saving a mesh move rewrites the WHOLE mapgeo — Map453's is
    /// 40 MB — so this can never be per-keystroke or per-drag; it waits for
    /// <see cref="AutoSaveDelaySeconds"/> of quiet and coalesces everything since the last save. Anyone who
    /// wants explicit control over when their project is written should leave it off.</para>
    /// </summary>
    public bool AutoSaveEdits { get; set; } = false;

    /// <summary>Quiet period before an auto-save fires, in seconds. Clamped to 2..120 on read: a value of
    /// zero would rewrite the mapgeo on every gizmo drag.</summary>
    public int AutoSaveDelaySeconds { get; set; } = 5;

    [JsonIgnore]
    public int EffectiveAutoSaveDelaySeconds => Math.Clamp(AutoSaveDelaySeconds, 2, 120);

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
    /// <summary>M133: where new projects and .fantome imports are created. Empty = the default
    /// Documents/ReyEngine Projects (which OneDrive may redirect - a plain local folder avoids
    /// sync locks and churn on 10k-file staging trees).</summary>
    public string ProjectsDirectory { get; set; } = "";

    /// <summary>M138: explicit WwiseConsole.exe / .wproj used for wav→wem conversion. Empty = probe
    /// the usual locations (an LtMAO install bundles both; a Wwise install provides the console).</summary>
    public string WwiseConsolePath { get; set; } = "";
    public string WwiseProjectPath { get; set; } = "";

    /// <summary>
    /// Overwrite this instance's values from another.
    ///
    /// <para>This is the ONLY path from the Preferences dialog to the live settings — the app does
    /// <c>Settings.CopyFrom(dialog.ToSettings()); Settings.Save();</c> — so a field missing here is a
    /// preference that cannot be changed AND cannot be persisted, with no error anywhere. M505 found
    /// exactly that: the M503c auto-save toggle was written, shown, and dropped on the way in.
    /// <c>EditorSettingsTests.CopyFromCarriesEverySetting</c> now fails if a new field is forgotten.</para>
    /// </summary>
    public void CopyFrom(EditorSettings s)
    {
        FlyForward = s.FlyForward; FlyBack = s.FlyBack; FlyLeft = s.FlyLeft; FlyRight = s.FlyRight;
        FlyUp = s.FlyUp; FlyDown = s.FlyDown; FocusSelected = s.FocusSelected;
        MouseLookSensitivity = s.MouseLookSensitivity; OrbitSensitivity = s.OrbitSensitivity;
        PanSensitivity = s.PanSensitivity; ZoomSensitivity = s.ZoomSensitivity;
        InvertLookY = s.InvertLookY; FlySpeed = s.FlySpeed; CullBackfacesDefault = s.CullBackfacesDefault;
        UseOpenGlViewport = s.UseOpenGlViewport;   // M762
        Theme = s.Theme;
        ThemeAccent = s.ThemeAccent; BackgroundImagePath = s.BackgroundImagePath;   // M683
        BackgroundImageOpacity = s.BackgroundImageOpacity; BackgroundGlass = s.BackgroundGlass;
        BackgroundImageStretch = s.BackgroundImageStretch;
        UpdateMode = s.UpdateMode;   // M681
        AutoUpdateHashes = s.AutoUpdateHashes;   // M731
        PreviewBackgroundMapFolder = s.PreviewBackgroundMapFolder; PreviewBackgroundEnabled = s.PreviewBackgroundEnabled;
        FirstRunCompleted = s.FirstRunCompleted;
        LastSeenFeatureVersion = s.LastSeenFeatureVersion;   // M593
        AutoSaveEdits = s.AutoSaveEdits; AutoSaveDelaySeconds = s.AutoSaveDelaySeconds;   // M505
        ProjectsDirectory = s.ProjectsDirectory;
        WwiseConsolePath = s.WwiseConsolePath; WwiseProjectPath = s.WwiseProjectPath;
    }
}
