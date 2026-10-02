using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M802: map EVENTS - content a map ships for one game event only (Summoner's Rift's Hall of Legends, the MSI trophies,
/// the esports sponsor banners), which an ordinary game never shows.
///
/// <para><b>The data.</b> A <c>MutatorMapVisibilityController</c> carries one <c>MutatorName</c> and is a yes/no the game
/// sets for the whole match, not a layer bit. Mesh groups, <c>MapParticle</c> placements, <c>GdsMapObject</c> props and
/// whole chunks name one as their visibility controller. A normal game has no event on, so what they gate is hidden;
/// before M802 the editor resolved these controllers as "always visible" and showed all of it.</para>
///
/// <para><b>What changes.</b> Every event is OFF when a map opens, so that content is hidden by default - the change on
/// Summoner's Rift, stated in the log on load. A checkbox per event under Visibility Layers > Events switches it on; the
/// choice feeds the same resolver the layers and the board stage do (<see cref="MapVisibilityResolver"/>), so the meshes,
/// the particle markers, Play All, the particle-derived sounds, the icons and picking all follow on both backends
/// (they read <c>CurrentModelSubmeshVisible</c> and the gates below). Session only: nothing is written to settings.</para>
///
/// <para><b>Not gated, by the data:</b> none of Riot's <c>MapAudio</c> sounds, <c>MapAnimatedProp</c> props or cubemap
/// probes names a visibility controller (0 of them across Map11, Map12, Map22, Map30 and Map453), so they have nothing for
/// an event to gate. M805: the banner props (<c>GdsMapObject</c>s with banner info) are drawn as their characters while their
/// event is on (MainWindowViewModel.MapBanners.cs). M806: the other level props are drawn with Props on, and an event gates one
/// only through its own or its chunk's controller - none shipped has either (MainWindowViewModel.LevelProps.cs).</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>One checkbox under Visibility Layers > Events.</summary>
    public sealed partial class MapEventViewModel : ObservableObject
    {
        private readonly MainWindowViewModel _owner;

        /// <summary>The raw <c>MutatorName</c>: what the game matches, and what the tooltip names.</summary>
        public string Name { get; }

        /// <summary>The readable form of <see cref="Name"/>.</summary>
        public string Label { get; }

        /// <summary>Read when the control is realised, not when the event is created: it counts what the open map
        /// holds then, which on a returning map tab is only true once that tab's content is back.</summary>
        public string Tooltip => _owner.DescribeMapEvent(Name);

        [ObservableProperty] private bool _isOn;

        internal MapEventViewModel(MainWindowViewModel owner, string name, bool isOn)
        {
            _owner = owner;
            Name = name;
            Label = MapEventNames.Label(name);
            _isOn = isOn;   // the field: no change to announce, the set is filled by the caller
        }

        partial void OnIsOnChanged(bool value) => _owner.ApplyMapEvent(this);
    }

    /// <summary>The open map's events, name order. Empty on every map without a mutator controller.</summary>
    public ObservableCollection<MapEventViewModel> MapEvents { get; } = new();

    /// <summary>True while the open map has at least one event. The Events block of the left panel binds to it.</summary>
    [NotifyPropertyChangedFor(nameof(ShowVisibilityLayers))]
    [ObservableProperty] private bool _hasMapEvents;

    /// <summary>The VISIBILITY LAYERS section shows when the map has a layer to pick or an event to tick - a map can
    /// have either without the other.</summary>
    public bool ShowVisibilityLayers => HasVisibilityAxes || HasMapEvents;

    /// <summary>The events that are ON. Empty when a map opens: a normal game has none.</summary>
    private readonly HashSet<string> _enabledEvents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What every map-visibility decision is asked with (<see cref="MapVisibilityResolver"/>'s
    /// <c>enabledEvents</c>). Never null, so event content is always judged.</summary>
    private IReadOnlySet<string> CurrentEnabledEvents => _enabledEvents;

    /// <summary>Publish the open map's events, with <paramref name="enabled"/> switched on (none for a map that is
    /// opening, the tab's own set for one returning). Runs when a map loads, when a map tab returns, and with null when
    /// the viewport is cleared.</summary>
    private void SetMapEvents(MapVisibilityControllers? controllers, IEnumerable<string>? enabled = null, bool announce = false)
    {
        _enabledEvents.Clear();
        MapEvents.Clear();
        var names = controllers?.EventNames ?? Array.Empty<string>();
        var on = enabled is null ? null : new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            bool isOn = on?.Contains(name) == true;
            if (isOn) _enabledEvents.Add(name);
            MapEvents.Add(new MapEventViewModel(this, name, isOn));
        }
        HasMapEvents = MapEvents.Count > 0;
        if (!announce || names.Count == 0) return;

        var hidden = CountHiddenByEvents();
        _log.Info("Events", $"{names.Count} event(s) gate content in this map: {string.Join(", ", names)}. All are OFF, as in a normal game, so "
            + $"{hidden.Meshes} mesh(es) ({hidden.Groups} draw group(s)), {hidden.Particles} particle placement(s) and {hidden.Sounds} sound(s) "
            + "start hidden." + (hidden.Banners > 0 ? $" So do {hidden.Banners} esports banner prop(s)." : "")   // M805
            + (hidden.LevelProps > 0 ? $" So do {hidden.LevelProps} level prop(s)." : "")                         // M806
            + " Tick an event under Visibility Layers > Events to show what it gates (session only).");
    }

    /// <summary>What a returning map tab's scene keeps of the checkboxes: the names that were ticked.</summary>
    private IReadOnlyList<string> SnapshotMapEvents() => _enabledEvents.ToArray();

    /// <summary>A returning map tab: the events it had ON, before <c>ApplyMapVisibility</c> reads them.</summary>
    private void RestoreMapEvents(IReadOnlyList<string>? enabled) =>
        SetMapEvents(_mapControllers, enabled ?? Array.Empty<string>());

    /// <summary>A checkbox moved: make the open map show (or stop showing) what that event gates.</summary>
    private void ApplyMapEvent(MapEventViewModel ev)
    {
        bool changed = ev.IsOn ? _enabledEvents.Add(ev.Name) : _enabledEvents.Remove(ev.Name);
        if (!changed) return;

        ApplyMapVisibility();   // meshes, particle markers and Play All, sounds, the inspector's why-hidden line

        var gated = CountGatedBy(ev.Name);
        var hidden = CountHiddenByEvents();
        _log.Info("Events", $"Event '{ev.Name}' {(ev.IsOn ? "ON" : "off")}: it gates {gated.Meshes} mesh(es) ({gated.Groups} draw group(s)), "
            + $"{gated.Particles} particle placement(s) and {gated.Sounds} sound(s)"
            + (gated.LevelProps > 0 ? $", {gated.LevelProps} level prop(s)" : "")                  // M806
            + (gated.Banners > 0 ? $", and {gated.Banners} esports banner prop(s)" : "") + ". "   // M805
            + $"Events on: {(_enabledEvents.Count == 0 ? "none" : string.Join(", ", _enabledEvents.Order(StringComparer.OrdinalIgnoreCase)))}; "
            + $"still hidden by events: {hidden.Meshes} mesh(es) ({hidden.Groups} draw group(s)), {hidden.Particles} particle placement(s), {hidden.Sounds} sound(s)"
            + (hidden.Banners > 0 ? $", {hidden.Banners} banner prop(s)" : "")
            + (hidden.LevelProps > 0 ? $", {hidden.LevelProps} level prop(s)" : "") + ".");   // M806
    }

    /// <summary>Does the event part of this controller let a particle's sound play? True with no event on the controller.
    /// Only the events: the dragon and baron layers never applied to a derived sound and still do not.</summary>
    private bool EventAllows(uint controllerHash) =>
        controllerHash == 0
        || (_visibilityResolver ??= new MapVisibilityResolver(_mapControllers, _mapVisibility)).EventsAllow(controllerHash, _enabledEvents);

    /// <summary>The tooltip of one checkbox.</summary>
    private string DescribeMapEvent(string name)
    {
        var gated = CountGatedBy(name);
        // M805: the banner props are drawn now, so they count - and the old "objects the editor does not show" aside went
        string gates = gated.Meshes + gated.Particles + gated.Sounds + gated.Banners + gated.LevelProps == 0
            ? "Nothing this editor draws is gated by it, so ticking it changes nothing on screen."
            : $"In this map it gates {gated.Meshes} mesh(es), {gated.Particles} particle placement(s) and {gated.Sounds} sound(s)"
              + (gated.LevelProps > 0 ? $", {gated.LevelProps} level prop(s) (drawn with Props on)" : "")   // M806
              + (gated.Banners > 0 ? $", and {gated.Banners} esports banner prop(s)." + DescribeMapBanners(gated.Banners) : ".");
        return $"Event \"{name}\" (MutatorMapVisibilityController). In a normal game no event is on, so the content it gates is hidden; "
            + $"tick it to show that content. {gates} Session only - never saved.";
    }

    /// <summary>How much of the open map the controllers that reach event <paramref name="name"/> gate.</summary>
    private (int Meshes, int Groups, int Particles, int Sounds, int Banners, int LevelProps) CountGatedBy(string name)
    {
        if (_mapControllers is not { } controllers) return default;
        bool Gated(uint hash) => hash != 0 && controllers.Resolve(hash).Mutators.Contains(name);
        return CountContent(Gated);
    }

    /// <summary>How much of the open map is hidden by events right now (with the events that are on).</summary>
    private (int Meshes, int Groups, int Particles, int Sounds, int Banners, int LevelProps) CountHiddenByEvents()
    {
        if (_mapControllers is null) return default;
        var resolver = _visibilityResolver ??= new MapVisibilityResolver(_mapControllers, _mapVisibility);
        return CountContent(hash => hash != 0 && !resolver.EventsAllow(hash, _enabledEvents));
    }

    private (int Meshes, int Groups, int Particles, int Sounds, int Banners, int LevelProps) CountContent(Func<uint, bool> controllerCounts)
    {
        int meshes = 0, groups = 0;
        if (_currentMap is { } map)
        {
            var gatedMeshes = map.Meshes.Where(m => controllerCounts(m.EffectiveController)).Select(m => m.Index).ToHashSet();
            meshes = gatedMeshes.Count;
            groups = map.Groups.Count(g => g.MeshIndex >= 0 ? gatedMeshes.Contains(g.MeshIndex) : controllerCounts(g.ControllerHash));
        }
        int particles = CurrentModelParticles?.Count(p => controllerCounts(p.VisibilityControllerHash)) ?? 0;
        int sounds = CurrentModelSounds?.Count(s => controllerCounts(s.VisibilityControllerHash)) ?? 0;
        int banners = _mapBanners.Count(b => controllerCounts(b.VisibilityControllerHash));   // M805
        int levelProps = _mapLevelProps.Count(p => controllerCounts(p.VisibilityControllerHash));   // M806: none shipped is gated
        return (meshes, groups, particles, sounds, banners, levelProps);
    }
}
