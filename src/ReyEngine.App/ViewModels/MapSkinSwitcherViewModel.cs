using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Formats.Meta;

namespace ReyEngine.App.ViewModels;

public sealed class MapSkinMapViewModel
{
    public required int MapId { get; init; }
    public required WadAssetEntry ShippingBinEntry { get; init; }
    public required MapSkinCatalog Catalog { get; init; }

    public string DisplayName => $"Map{MapId}  -  {FriendlyName(MapId)}  ({Catalog.MapStringId})  -  {Catalog.Skins.Count} skin(s)";

    private static string FriendlyName(int mapId) => mapId switch
    {
        11 => "Summoner's Rift",
        12 => "Howling Abyss",
        21 => "Nexus Blitz",
        30 => "Arena",
        33 => "Swarm",
        35 => "Brawl",
        _ => "Shipping Map",
    };
}

public sealed class MapSkinOptionViewModel
{
    public required MapSkinInfo Info { get; init; }
    public string DisplayName => Info.DisplayName;
    public string Detail => Info.MapContainerLink is { Length: > 0 }
        ? $"Loads {Info.MapContainerLink} with {Info.PropertyCount} complete skin settings."
        : $"Uses the map's legacy/default geometry with {Info.PropertyCount} complete skin settings.";
}

public sealed record MapSkinApplyRequest(
    MapSkinMapViewModel Map,
    MapSkinOptionViewModel Target,
    MapSkinOptionViewModel Source);

/// <summary>Crash-safe UI over <see cref="MapSkinSwitcher"/>. The host owns merged-view validation and saving.</summary>
public sealed partial class MapSkinSwitcherViewModel : ObservableObject
{
    public ObservableCollection<MapSkinMapViewModel> Maps { get; } = new();
    public ObservableCollection<MapSkinOptionViewModel> TargetSkins { get; } = new();
    public ObservableCollection<MapSkinOptionViewModel> SourceSkins { get; } = new();

    [ObservableProperty] private MapSkinMapViewModel? _selectedMap;
    [ObservableProperty] private MapSkinOptionViewModel? _selectedTarget;
    [ObservableProperty] private MapSkinOptionViewModel? _selectedSource;
    [ObservableProperty] private string _status = "Choose the skin slot the game normally selects, then the complete skin to load instead.";
    [ObservableProperty] private bool _running;

    public Func<MapSkinApplyRequest, Task<string>>? ApplySwap;

    public bool CanApply => !Running && SelectedMap is not null && SelectedTarget is not null
        && SelectedSource is not null && SelectedTarget.Info.PathHash != SelectedSource.Info.PathHash;
    public string SwapSummary => SelectedTarget is null || SelectedSource is null
        ? "Select a target and source skin."
        : $"{SelectedTarget.Info.Name} keeps its slot identity; every other setting is cloned from {SelectedSource.Info.Name}.";
    public string TargetDetail => SelectedTarget?.Detail ?? "";
    public string SourceDetail => SelectedSource?.Detail ?? "";

    public MapSkinSwitcherViewModel(IEnumerable<MapSkinMapViewModel> maps)
    {
        foreach (var map in maps.OrderBy(map => map.MapId)) Maps.Add(map);
        SelectedMap = Maps.FirstOrDefault();
    }

    partial void OnSelectedMapChanged(MapSkinMapViewModel? value)
    {
        TargetSkins.Clear();
        SourceSkins.Clear();
        if (value is not null)
        {
            foreach (var skin in value.Catalog.Skins)
                TargetSkins.Add(new MapSkinOptionViewModel { Info = skin });
            SelectedTarget = TargetSkins.FirstOrDefault(s => s.Info.Name.Equals("Default", StringComparison.OrdinalIgnoreCase))
                ?? TargetSkins.FirstOrDefault();
        }
        else SelectedTarget = null;
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnSelectedTargetChanged(MapSkinOptionViewModel? value)
    {
        uint? previousSource = SelectedSource?.Info.PathHash;
        SourceSkins.Clear();
        if (SelectedMap is not null)
            foreach (var skin in SelectedMap.Catalog.Skins.Where(s => s.PathHash != value?.Info.PathHash))
                SourceSkins.Add(new MapSkinOptionViewModel { Info = skin });
        SelectedSource = SourceSkins.FirstOrDefault(s => s.Info.PathHash == previousSource)
            ?? SourceSkins.FirstOrDefault();
        OnPropertyChanged(nameof(TargetDetail));
        OnPropertyChanged(nameof(SwapSummary));
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnSelectedSourceChanged(MapSkinOptionViewModel? value)
    {
        OnPropertyChanged(nameof(SourceDetail));
        OnPropertyChanged(nameof(SwapSummary));
        OnPropertyChanged(nameof(CanApply));
    }

    partial void OnRunningChanged(bool value) => OnPropertyChanged(nameof(CanApply));

    [RelayCommand]
    private async Task Apply()
    {
        if (!CanApply || ApplySwap is null) return;
        Running = true;
        try
        {
            Status = "Validating the complete source skin against the mounted game files...";
            Status = await ApplySwap(new MapSkinApplyRequest(SelectedMap!, SelectedTarget!, SelectedSource!));
        }
        catch (Exception ex) { Status = $"Not changed: {ex.Message}"; }
        finally { Running = false; }
    }
}
