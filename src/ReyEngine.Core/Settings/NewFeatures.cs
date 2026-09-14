namespace ReyEngine.Core.Settings;

/// <summary>One feature that should be pointed out to the user, and the release it arrived in.</summary>
/// <param name="Id">Stable key used from XAML, e.g. <c>blender-link</c>. Never rename one: the id is what
/// a control binds to, and a rename silently stops highlighting it.</param>
/// <param name="Version">The release that introduced it, e.g. <c>0.4.0</c>.</param>
/// <param name="Label">What it is, in the words a mod author would use. For the What's New list.</param>
public sealed record NewFeature(string Id, string Version, string Label);

/// <summary>
/// M593: which features are still worth pointing out, given what this user has already seen.
///
/// <para>Version-aware rather than release-specific: a feature carries the version it arrived in, the user
/// carries the newest version they have acknowledged, and a feature is "new" when the first is newer than
/// the second. Adding v0.5.0 features later is one more line each — nothing here is v0.4.0 shaped.</para>
///
/// <para><b>What is deliberately NOT in the registry.</b> Anything internal, experimental, unfinished or
/// confidential. The registry is the thing that decides what glows AND what a What's New list could show,
/// so an entry here is a decision to expose the feature's name publicly. Keeping the list explicit — rather
/// than deriving it from commits or command names — is what stops an internal tool leaking into the UI by
/// accident.</para>
/// </summary>
public static class NewFeatures
{
    /// <summary>The release whose highlights are currently on offer. Bumped when a release ADDS entries -
    /// a fix-only release (0.4.6) leaves it, so the What's New list keeps its newest header and nothing
    /// glows for a release that changed no entry point.</summary>
    public const string CurrentVersion = "0.4.7";

    /// <summary>
    /// The public, user-facing features introduced in <see cref="CurrentVersion"/>.
    ///
    /// <para>Each id is bound by one control in the UI, so this list and the marked controls have to move
    /// together: an entry with nothing bound to it glows nowhere, and a control bound to an id that is not
    /// here goes quiet (see <see cref="IsNew"/>). Fifteen entries for fifteen controls.</para>
    ///
    /// <para>What is NOT here is the point of the list — see the class remarks. Internal work, research,
    /// renderer plumbing and anything unfinished stays out, however large it was.</para>
    /// </summary>
    public static IReadOnlyList<NewFeature> All => _registry ?? Shipping;

    /// <summary>M689: the registry a test swapped in, or null for the shipping list. This used to be an
    /// auto-property initialised from <see cref="Shipping"/> - which is declared BELOW it, so C#'s
    /// textual static-initialisation order set it to null in every real process. Every IsNew() threw,
    /// Avalonia swallowed the throw as a binding error, and no menu entry ever glowed between 0.4.0
    /// and 0.4.4. The tests never saw it because each test class calls SetRegistry first. A headless
    /// run of the What's New window - a fresh process with nothing reset - is what caught it.</summary>
    private static IReadOnlyList<NewFeature>? _registry;

    /// <summary>The list this build actually ships, kept separate from <see cref="All"/> so it stays
    /// inspectable after a test has swapped the registry out — <see cref="SetRegistry"/> mutates static
    /// state, and a test that asserts against the real list must not be at the mercy of run order.</summary>
    public static IReadOnlyList<NewFeature> Shipping { get; } = new NewFeature[]
    {
        new("blender-link",     "0.4.0", "Live Blender link — edit map geometry in Blender and push it back"),
        new("face-editing",     "0.4.0", "Face editing in the viewport — move, extrude and inset faces"),
        new("navgrid-overlay",  "0.4.0", "NavGrid overlay — see the gameplay bush and every navgrid layer"),
        new("legacy-map-port",  "0.4.0", "Legacy map port now carries particles, sounds and lights across"),
        new("ritobin-editor",   "0.4.0", "Edit a .bin as ritobin text and write it back byte-exactly"),
        new("material-browser", "0.4.0", "Material browser — audit every material and copy one look onto many"),
        new("second-uv",        "0.4.0", "Second UV (Texcoord7) viewer and editor"),
        new("patch-update",     "0.4.0", "Patch Update Wizard — carry a mod onto a new Riot patch"),
        new("ltk-manager",      "0.4.0", "Send to LTK Manager — create or update a workshop mod in place"),
        // 0.4.1. Only the two entry points this release ADDS to the main window are here: the arena, the
        // bulk edits and the state switch live inside those windows and have no control here to glow on,
        // and an entry with nothing bound to it glows nowhere.
        new("character-editor", "0.4.1", "Character Editor — preview a champion on Riot's own shaders and edit its materials"),
        new("cinematic",        "0.4.1", "Cinematic Capture — fly a camera on keyframes and export a PNG sequence"),
        // 0.4.2. The switcher window is not new, but what it can do is: it now shows and can carry the
        // turret / minion / nexus skins a map skin forces, which is the thing a user reported missing.
        new("map-skin-units",   "0.4.2", "Map Skin Switcher — see and carry a map skin's turret, minion and nexus skins"),
        // 0.4.3. Three entry points in this window whose CAPABILITY changed, not three new windows. The
        // gizmo, the icons and the D3D11 debug views are the rest of this release and have no control of
        // their own to glow on - they are how everything already here is drawn.
        new("workshop-shelf",   "0.4.3", "Workshop — keep your own .troybin effects, custom-bin particles and meshes on a shelf"),
        new("add-mesh-library", "0.4.3", "Add Mesh — search a whole mapgeo, import .skn, and carry the original material across"),
        new("light-range",      "0.4.3", "Overlays — see how far each point light reaches, and hide icons behind geometry"),
        // 0.4.4. Two entry points in this window whose capability changed. The installer, the update dialog,
        // the material cleanup and the props are the rest of this release and have no control here to glow
        // on: the first two arrive by themselves, the other two live inside windows of their own.
        new("blender-addon",    "0.4.4", "Install the Blender add-on from here — every Blender on this PC, one click"),
        new("look",             "0.4.4", "Preferences — eight palettes, an accent of your own, a picture behind the editor, and how updates arrive"),
        new("props-animated",   "0.4.4", "Overlays — placed mobs and props draw on Riot's shaders, animated and lit by the map, and open in the Character Editor"),
        // 0.4.5. The list itself is the entry point; the GIF backdrop lives inside Preferences, whose
        // entry above already glows for the look.
        new("whats-new",        "0.4.5", "Help ▸ What's New — every feature by release, and the one Got it that stops the highlights"),
        // 0.4.7. Two entry points this release ADDS to the Tools menu, and one whose capability changed:
        // the hash sync keeps itself current now. The rest of the release - the particle rig and curve
        // editor, the Character Viewer's backdrop, chromas, the map bins the patch updater re-does - lives
        // inside windows whose entries already glow, or arrives by itself, and has no control here.
        new("character-creator", "0.4.7", "Character Creator — an old character folder, from any patch, becomes a prop your map can place"),
        new("add-prop",          "0.4.7", "Add prop to map — place any character the map's package carries as scenery at the gizmo"),
        new("hash-updates",      "0.4.7", "Hashes & Names — hash tables and meta classes keep themselves current at startup; the switch is in Preferences ▸ Updates"),
    };

    /// <summary>Replace the registry. Exists so tests can drive the logic without depending on whatever
    /// the shipping list happens to contain.</summary>
    public static void SetRegistry(IReadOnlyList<NewFeature> features) => _registry = features;

    /// <summary>
    /// Should <paramref name="featureId"/> be highlighted for a user whose newest acknowledged release is
    /// <paramref name="lastSeenVersion"/>?
    /// </summary>
    /// <remarks>
    /// An unknown id is NOT new. That matters: a control bound to a typo'd or retired id must go quiet
    /// rather than glow forever, because nothing would ever mark it seen.
    /// </remarks>
    public static bool IsNew(string? featureId, string? lastSeenVersion)
    {
        if (string.IsNullOrWhiteSpace(featureId)) return false;
        var feature = All.FirstOrDefault(f =>
            string.Equals(f.Id, featureId, StringComparison.OrdinalIgnoreCase));
        if (feature is null) return false;
        return Compare(feature.Version, lastSeenVersion) > 0;
    }

    /// <summary>Everything the user has not acknowledged yet, newest release first.</summary>
    public static IReadOnlyList<NewFeature> Unseen(string? lastSeenVersion) =>
        All.Where(f => Compare(f.Version, lastSeenVersion) > 0)
           .OrderByDescending(f => f.Version, VersionOrder)
           .ToList();

    /// <summary>Is there anything to point out at all?</summary>
    public static bool AnyUnseen(string? lastSeenVersion) =>
        All.Any(f => Compare(f.Version, lastSeenVersion) > 0);

    private static readonly IComparer<string> VersionOrder =
        Comparer<string>.Create((a, b) => Compare(a, b));

    /// <summary>
    /// Dotted-numeric version compare. An empty or unparsable "last seen" sorts BELOW everything, so a
    /// fresh install and every settings file written before this existed both see the current highlights —
    /// which is the desired behaviour for a user updating into the release.
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var a = Parts(left);
        var b = Parts(right);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static int[] Parts(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return Array.Empty<int>();
        // Tolerate a leading 'v' and a trailing suffix ("0.4.0-beta") - only the numeric run matters.
        string text = version.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) text = text[..cut];
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<int>(parts.Length);
        foreach (string p in parts)
        {
            if (!int.TryParse(p, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int n)) break;
            result.Add(n);
        }
        return result.ToArray();
    }
}
