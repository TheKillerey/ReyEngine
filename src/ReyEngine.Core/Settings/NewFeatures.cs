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
    /// <summary>The release whose highlights are currently on offer. Bump alongside the app version.</summary>
    public const string CurrentVersion = "0.4.0";

    /// <summary>
    /// The public, user-facing features introduced in <see cref="CurrentVersion"/>.
    ///
    /// <para>Empty until the release list is agreed. Populating it is a deliberate editorial act, not a
    /// side effect of writing code — see the class remarks.</para>
    /// </summary>
    public static IReadOnlyList<NewFeature> All { get; private set; } = Array.Empty<NewFeature>();

    /// <summary>Replace the registry. Exists so tests can drive the logic without depending on whatever
    /// the shipping list happens to contain.</summary>
    public static void SetRegistry(IReadOnlyList<NewFeature> features) => All = features;

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
