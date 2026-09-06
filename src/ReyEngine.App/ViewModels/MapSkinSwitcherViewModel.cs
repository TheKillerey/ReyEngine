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

    /// <summary>M649: the turret/minion/nexus skins this slot forces - the thing a user means by
    /// "swapping the turrets". Most slots force none, which is worth saying out loud: swapping to one of
    /// those can never bring turrets with it, because it has none to bring.</summary>
    public string CharacterDetail => Info.CharacterSkins.Count == 0
        ? "Forces no character skins - its turrets, minions and nexus are the map's defaults."
        : Info.CharacterSummary;

    public bool ForcesCharacterSkins => Info.CharacterSkins.Count > 0;

    /// <summary>The sentence shown next to the carry option once this slot is the source.</summary>
    public string Detail_CharacterCarry() =>
        $"Routes {Info.CharacterSkins.Count} character skin(s) from {Info.Name} onto every slot: "
        + string.Join(", ", Info.CharacterSkins.Take(6).Select(o => o.DisplayName))
        + (Info.CharacterSkins.Count > 6 ? $", +{Info.CharacterSkins.Count - 6} more" : "")
        + ". Slots that shipped without a character-skin field are GIVEN one - Riot's own data has no "
        + "example of that, so check it in game before shipping a mod with it.";
}

public sealed record MapSkinApplyRequest(
    MapSkinMapViewModel Map,
    MapSkinOptionViewModel Target,
    MapSkinOptionViewModel Source,
    /// <summary>M649: also route the source's turret/minion/nexus skins. Off by default.</summary>
    bool CarryCharacterSkins = false);

/// <summary>Crash-safe UI over <see cref="MapSkinSwitcher"/>. The host owns merged-view validation and saving.</summary>
public sealed partial class MapSkinSwitcherViewModel : ObservableObject
{
    public ObservableCollection<MapSkinMapViewModel> Maps { get; } = new();
    public ObservableCollection<MapSkinOptionViewModel> TargetSkins { get; } = new();
    public ObservableCollection<MapSkinOptionViewModel> SourceSkins { get; } = new();

    [ObservableProperty] private MapSkinMapViewModel? _selectedMap;
    [ObservableProperty] private MapSkinOptionViewModel? _selectedTarget;
    [ObservableProperty] private MapSkinOptionViewModel? _selectedSource;
    [ObservableProperty] private string _status = "Choose the current/base skin, then the complete environment it should load.";
    [ObservableProperty] private bool _running;

    /// <summary>M649: bring the source skin's turret/minion/nexus skins across as well.</summary>
    [ObservableProperty] private bool _carryCharacterSkins;

    public Func<MapSkinApplyRequest, Task<string>>? ApplySwap;

    public bool CanApply => !Running && SelectedMap is not null && SelectedTarget is not null
        && SelectedSource is not null && SelectedTarget.Info.PathHash != SelectedSource.Info.PathHash;
    public string SwapSummary => SelectedTarget is null || SelectedSource is null
        ? "Select a target and source skin."
        : $"Every slot keeps its identity while {SelectedSource.Info.Name}'s environment, compatible gameplay IDs, music and ambience are routed together.";
    public string TargetDetail => SelectedTarget?.Detail ?? "";
    public string SourceDetail => SelectedSource?.Detail ?? "";

    /// <summary>M649: what carrying the character skins would actually do, given the chosen source.</summary>
    public string CharacterSkinDetail => SelectedSource is not { } source
        ? ""
        : source.ForcesCharacterSkins
            ? source.Detail_CharacterCarry()
            : $"{source.Info.Name} forces no character skins, so this changes nothing - its turrets, minions "
              + "and nexus are already the map's defaults. Give it some in the bin first.";

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
            foreach (var skin in SelectedMap.Catalog.Skins.Where(s => s.PathHash != value?.Info.PathHash
                && s.MapContainerLink is not null))
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
        OnPropertyChanged(nameof(CharacterSkinDetail));   // M649
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
            Status = "Building and validating the crash-safe environment, gameplay and audio overrides...";
            Status = await ApplySwap(new MapSkinApplyRequest(SelectedMap!, SelectedTarget!, SelectedSource!,
                CarryCharacterSkins));
        }
        catch (Exception ex) { Status = $"Not changed: {ex.Message}"; }
        finally { Running = false; }
    }
}
