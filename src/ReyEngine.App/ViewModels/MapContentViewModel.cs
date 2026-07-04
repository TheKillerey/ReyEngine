using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>One placed particle in the outliner tree (M35): wraps a <see cref="MapParticlePlacement"/> and
/// carries a live move <see cref="Offset"/> so it can be repositioned like a mesh.</summary>
public sealed partial class ParticlePlacementViewModel : ObservableObject
{
    public required MapParticlePlacement Placement { get; init; }
    public string Name => Placement.Name;
    public string SystemName => Placement.SystemName;
    [ObservableProperty] private bool _isSelected;

    public Vector3 Offset;                                   // accumulated move (world space), default zero
    public Vector3 CurrentPosition => Placement.Position + Offset;
    public bool IsMoved => Offset != Vector3.Zero;
}

/// <summary>A particle-system group folder (all placements that reference the same VFX system).</summary>
public sealed class ParticleSystemGroupViewModel
{
    public required string SystemName { get; init; }
    public ObservableCollection<ParticlePlacementViewModel> Placements { get; } = new();
    public string Header => $"{SystemName} — {Placements.Count}";
}

/// <summary>One placed animated prop / character in the outliner (M38).</summary>
public sealed partial class AnimatedPropViewModel : ObservableObject
{
    public required MapAnimatedProp Prop { get; init; }
    public string Name => Prop.Display;
    public string Info => Prop.CharacterRecord;
    public Vector3 Position => Prop.Position;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A prop group folder (all placements of the same character).</summary>
public sealed class AnimatedPropGroupViewModel
{
    public required string CharacterName { get; init; }
    public ObservableCollection<AnimatedPropViewModel> Props { get; } = new();
    public string Header => $"{CharacterName} — {Props.Count}";
}

/// <summary>One placed cubemap reflection probe in the outliner (M38).</summary>
public sealed partial class CubemapProbeViewModel : ObservableObject
{
    public required MapCubemapProbe Probe { get; init; }
    public string Name => Probe.Name;
    public string Info => Probe.CubemapFile ?? "(no cubemap)";
    public Vector3 Position => Probe.Position;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>One VFX system in a loaded champion skin's effect library (M37) — select it to play it.</summary>
public sealed class VfxSystemItemViewModel
{
    public required uint Hash { get; init; }
    public required string Name { get; init; }
    public required int EmitterCount { get; init; }
    public string Display => $"{Name}   ·   {EmitterCount} emitter{(EmitterCount == 1 ? "" : "s")}";
}

public sealed partial class MapPieceViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string Info { get; init; }
    public int MeshIndex { get; init; } = -1;   // index into MapGeoAsset.Meshes (for selection/move)

    /// <summary>Multi-select highlight state (M30): mirrors the viewport SelectionSet so the tree row
    /// shows as selected even when it isn't the TreeView's single SelectedItem.</summary>
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A visibility layer group (one distinct dragon bitmask) and the meshes it contains.</summary>
public sealed class MapLayerGroupViewModel
{
    public required string Name { get; init; }   // e.g. "Ocean — 137 meshes"
    public required int Bit { get; init; }        // the distinct VisibilityFlags value
    public ObservableCollection<MapPieceViewModel> Meshes { get; } = new();
}

public sealed class RecentProjectViewModel
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/', '\\'));
    public RecentProjectViewModel(string path) => Path = path;
}

/// <summary>
/// Left "Map Content" panel (Unreal World-Outliner style): the project's map geometry files, and —
/// when one is loaded — its mesh groups as a scene outline.
/// </summary>
public sealed partial class MapContentViewModel : ViewModelBase
{
    public ObservableCollection<AssetNodeViewModel> Maps { get; } = new();
    public ObservableCollection<MapPieceViewModel> Pieces { get; } = new();
    public ObservableCollection<MapLayerGroupViewModel> LayerGroups { get; } = new();
    /// <summary>Placed particle systems grouped by VFX system (M35), shown as a "Particles" folder in the tree.</summary>
    public ObservableCollection<ParticleSystemGroupViewModel> ParticleGroups { get; } = new();

    [ObservableProperty] private bool _hasParticles;
    public int ParticleCount => ParticleGroups.Sum(g => g.Placements.Count);

    /// <summary>Placed animated props / characters grouped by character (M38).</summary>
    public ObservableCollection<AnimatedPropGroupViewModel> PropGroups { get; } = new();
    /// <summary>Placed cubemap reflection probes (M38).</summary>
    public ObservableCollection<CubemapProbeViewModel> Probes { get; } = new();
    [ObservableProperty] private bool _hasProps;
    [ObservableProperty] private bool _hasProbes;
    public int PropCount => PropGroups.Sum(g => g.Props.Count);
    public IEnumerable<AnimatedPropViewModel> AllProps => PropGroups.SelectMany(g => g.Props);

    public void SetProps(IReadOnlyList<MapAnimatedProp> props)
    {
        PropGroups.Clear();
        foreach (var g in props.GroupBy(p => p.CharacterName).OrderBy(g => g.Key, System.StringComparer.OrdinalIgnoreCase))
        {
            var grp = new AnimatedPropGroupViewModel { CharacterName = g.Key };
            foreach (var p in g) grp.Props.Add(new AnimatedPropViewModel { Prop = p });
            PropGroups.Add(grp);
        }
        HasProps = PropGroups.Count > 0;
        OnPropertyChanged(nameof(PropCount));
    }

    public void SetProbes(IReadOnlyList<MapCubemapProbe> probes)
    {
        Probes.Clear();
        foreach (var p in probes) Probes.Add(new CubemapProbeViewModel { Probe = p });
        HasProbes = Probes.Count > 0;
    }

    public void SetParticles(IReadOnlyList<MapParticlePlacement> particles)
    {
        ParticleGroups.Clear();
        foreach (var g in particles.GroupBy(p => p.SystemName).OrderBy(g => g.Key, System.StringComparer.OrdinalIgnoreCase))
        {
            var grp = new ParticleSystemGroupViewModel { SystemName = g.Key };
            foreach (var p in g.OrderBy(p => p.Name, System.StringComparer.OrdinalIgnoreCase))
                grp.Placements.Add(new ParticlePlacementViewModel { Placement = p });
            ParticleGroups.Add(grp);
        }
        HasParticles = ParticleGroups.Count > 0;
        OnPropertyChanged(nameof(ParticleCount));
    }

    public IEnumerable<ParticlePlacementViewModel> AllParticles => ParticleGroups.SelectMany(g => g.Placements);

    [ObservableProperty] private string _mapName = "";
    [ObservableProperty] private bool _hasMap;
    [ObservableProperty] private string _summary = "Open a project folder to list its maps.";

    /// <summary>Invoked when the user picks a map to load.</summary>
    public Action<AssetNodeViewModel>? OpenMap { get; set; }

    public void SetMaps(IEnumerable<AssetNodeViewModel> mapNodes)
    {
        Maps.Clear();
        foreach (var n in mapNodes) Maps.Add(n);
        Summary = Maps.Count == 0 ? "No .mapgeo maps in this project." : $"{Maps.Count} map(s) in project — double-click to load.";
    }

    public void ShowMap(string name, IReadOnlyList<MapPieceViewModel> pieces)
    {
        MapName = name;
        Pieces.Clear();
        foreach (var p in pieces) Pieces.Add(p);
        HasMap = true;
    }

    public void SetLayerGroups(IEnumerable<MapLayerGroupViewModel> groups)
    {
        LayerGroups.Clear();
        foreach (var g in groups) LayerGroups.Add(g);
    }

    public void ClearMap()
    {
        MapName = "";
        Pieces.Clear();
        LayerGroups.Clear();
        ParticleGroups.Clear();
        HasParticles = false;
        PropGroups.Clear();
        Probes.Clear();
        HasProps = false;
        HasProbes = false;
        HasMap = false;
    }

    public void Clear()
    {
        Maps.Clear();
        ClearMap();
        Summary = "Open a project folder to list its maps.";
    }

    [RelayCommand]
    private void Open(AssetNodeViewModel? node)
    {
        if (node is not null) OpenMap?.Invoke(node);
    }
}
