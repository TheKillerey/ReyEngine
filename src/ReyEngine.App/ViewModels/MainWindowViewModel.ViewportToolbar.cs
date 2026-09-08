namespace ReyEngine.App.ViewModels;

/// <summary>
/// M577: the viewport toolbar's own state.
///
/// <para>The toolbar had grown to 28 controls across two rows and still overflowed the viewport. Most of
/// them are toggles that get set once and left alone, so they now live in three grouped menus — Display,
/// Overlays, Tools — and only the controls that get clicked while working stay on the bar.</para>
///
/// <para>Collapsing toggles into a menu costs something: you can no longer see at a glance what is on.
/// <see cref="ActiveOverlayCount"/> is the compensation — the Overlays button carries the number, so the
/// state is still readable with the menu shut.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// How many of the Overlays menu's toggles are on.
    ///
    /// <para>Deliberately exactly the menu's contents and nothing else, so the badge and the menu can never
    /// disagree. The NavGrid overlay is not counted: it is its own control with its own layer list, and
    /// folding it in would make the number stop matching what the menu shows.</para>
    ///
    /// <para>M659: nor is <c>IconsThroughWalls</c>, which shares the menu but is not an overlay - it
    /// changes how the icons already counted are drawn, and counting it would say "one more thing is on"
    /// while nothing extra appears. The light RANGE is counted, because it is a thing that draws - but
    /// only while the markers it belongs to are on, which is the condition under which it draws at all.</para>
    /// </summary>
    public int ActiveOverlayCount =>
        (ShowSoundIcons ? 1 : 0) + (ShowPropIcons ? 1 : 0) + (ShowParticles ? 1 : 0)
        + (ShowLightMarkers ? 1 : 0) + (ShowLightRanges && ShowLightMarkers ? 1 : 0)
        + (ShowPlaceables ? 1 : 0) + (ShowBounds ? 1 : 0)
        + (ShowBones ? 1 : 0) + (ShowBucketGrid ? 1 : 0) + (ShowBakeBox ? 1 : 0);

    public bool HasActiveOverlays => ActiveOverlayCount > 0;

    /// <summary>The count as it appears on the button.</summary>
    public string OverlayBadge => ActiveOverlayCount.ToString();
}
