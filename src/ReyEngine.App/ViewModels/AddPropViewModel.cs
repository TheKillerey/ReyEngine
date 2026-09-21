using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meshes;

namespace ReyEngine.App.ViewModels;

/// <summary>One character the open map can place, listed from paths alone.</summary>
public sealed class PropCharacterRowViewModel
{
    public required CharacterEntry Entry { get; init; }
    public string Name => Entry.Name;
    public IReadOnlyList<CharacterSkinRef> Skins => Entry.Skins;
    public string Detail => Skins.Count == 1 ? "1 skin" : $"{Skins.Count} skins";
    public override string ToString() => Name;
}

/// <summary>One skin of the selected character, as the list shows it.</summary>
public sealed class PropSkinRowViewModel
{
    public required CharacterSkinRef Reference { get; init; }
    public required string Label { get; init; }
    public override string ToString() => Label;
}

/// <summary>What the host is asked to place.</summary>
/// <param name="AsAnimatedProp">M747: write a MapAnimatedProp (client-side decoration) rather than a scenery
/// character placement.</param>
public sealed record AddPropRequest(string Character, string CharacterRecord, string Skin, string? IdleClip,
    bool AsAnimatedProp = true, float AppearAfterSeconds = 0f);

/// <summary>
/// M697: add a prop to the map - a character the map's own package already carries, placed as scenery.
///
/// <para>The list costs nothing to build: <see cref="CharacterCatalog"/> reads the resolved asset paths
/// and parses no bins, which matters because a map WAD carries hundreds of characters. Only the skin you
/// select is read, and then for three things the placement actually needs: the mesh (a skin with none
/// cannot draw), the clips its animation graph names (the idle the placement will play), and its object
/// path in the form the hash dictionary spells it, because the placement stores that path as a STRING and
/// a lower-cased one would read back oddly everywhere the editor shows it.</para>
///
/// <para>This is the same placement a freshly imported character gets - one verb in one writer - so a
/// character made by the Character Creator appears in this list as soon as it is staged.</para>
/// </summary>
public sealed partial class AddPropViewModel : ObservableObject
{
    /// <summary>Read one asset of the open project by wad path.</summary>
    public Func<string, byte[]?>? ReadAsset;
    public Func<uint, string?>? ResolveBinName;
    public Func<ulong, string?>? ResolveWadPath;
    /// <summary>Host: place it. Returns the line to show; throws with the reason when it cannot.</summary>
    public Func<AddPropRequest, Task<string>>? Add;

    private IReadOnlyList<PropCharacterRowViewModel> _all = Array.Empty<PropCharacterRowViewModel>();

    public ObservableCollection<PropCharacterRowViewModel> Characters { get; } = new();
    public ObservableCollection<PropSkinRowViewModel> Skins { get; } = new();
    public ObservableCollection<string> Clips { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private PropCharacterRowViewModel? _selectedCharacter;
    [ObservableProperty] private PropSkinRowViewModel? _selectedSkin;
    [ObservableProperty] private string? _selectedClip;
    [ObservableProperty] private string _status = "Pick a character the map carries, then add it where the gizmo is.";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private bool _busy;
    /// <summary>M747: place as a MapAnimatedProp (client-side) rather than a scenery character.</summary>
    [ObservableProperty] private bool _placeAsAnimatedProp = true;
    /// <summary>M748 (experimental): seconds of game time before the prop appears; 0 = always.</summary>
    [ObservableProperty] private decimal _appearAfterSeconds;

    /// <summary>The skin's object path, in the spelling the hash dictionary knows.</summary>
    public string? SkinObjectPath { get; private set; }
    public string? CharacterRecordPath { get; private set; }

    public bool HasProblem => Problem.Length > 0;
    public bool CanAdd => !Busy && Add is not null && SkinObjectPath is not null && Problem.Length == 0;
    public int CharacterCount => _all.Count;

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanAdd));
    partial void OnProblemChanged(string value) { OnPropertyChanged(nameof(HasProblem)); OnPropertyChanged(nameof(CanAdd)); }
    partial void OnSearchChanged(string value) => ApplyFilter();

    /// <summary>List every character in the open map's package. Paths only - nothing is parsed here.</summary>
    public void Load(IEnumerable<string> resolvedPaths)
    {
        _all = CharacterCatalog.Characters(resolvedPaths, championName: null)
            .Where(c => c.Skins.Any(s => !s.IsRoot))
            .Select(c => new PropCharacterRowViewModel { Entry = c })
            .ToArray();
        ApplyFilter();
        Status = _all.Count == 0
            ? "This map's package holds no characters. Import one with Tools ▸ Import character folder…, or open a map that ships mobs."
            : $"{_all.Count:n0} character(s) in this map's package. Pick one, then add it where the gizmo is.";
    }

    private void ApplyFilter()
    {
        string search = Search.Trim();
        var kept = _all.Where(c => search.Length == 0 || c.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        Characters.Clear();
        foreach (var c in kept) Characters.Add(c);
        if (SelectedCharacter is null || !kept.Contains(SelectedCharacter)) SelectedCharacter = kept.FirstOrDefault();
        OnPropertyChanged(nameof(CharacterCount));
    }

    partial void OnSelectedCharacterChanged(PropCharacterRowViewModel? value)
    {
        Skins.Clear();
        if (value is not null)
            foreach (var skin in value.Skins.Where(s => !s.IsRoot))
                Skins.Add(new PropSkinRowViewModel { Reference = skin, Label = SkinLabel(skin) });
        SelectedSkin = Skins.FirstOrDefault();
    }

    private static string SkinLabel(CharacterSkinRef skin) =>
        skin.Number <= 0 ? "Skin0 (base)" : $"Skin{skin.Number}";

    partial void OnSelectedSkinChanged(PropSkinRowViewModel? value)
    {
        Clips.Clear();
        SelectedClip = null;
        SkinObjectPath = null;
        CharacterRecordPath = null;
        Detail = "";
        Problem = "";
        OnPropertyChanged(nameof(CanAdd));
        if (value is null || SelectedCharacter is not { } character || ReadAsset is null) return;

        byte[]? bin;
        try { bin = ReadAsset(value.Reference.BinPath); }
        catch (Exception ex) { Problem = $"{value.Reference.BinPath} could not be read: {ex.Message}"; return; }
        if (bin is null) { Problem = $"{value.Reference.BinPath} is not in this map."; return; }

        var mesh = SkinMeshExtractor.Extract(bin, ResolveWadPath);
        if (mesh?.SimpleSkin is not { Length: > 0 } meshPath)
        { Problem = "This skin names no mesh, so a placement of it would draw nothing."; return; }

        // the object paths, spelled the way the dictionary knows them - the placement stores STRINGS
        SkinObjectPath = ObjectPathOf(bin) ?? FallbackSkinPath(character.Name, value.Reference);
        CharacterRecordPath = RecordPathOf(character.Name);

        // The NAMES the graph declares, not the clips that have a file behind them: a placement asks the
        // game for a clip BY NAME, and the Golem's own Idle1 is a selector with no file of its own (M697).
        var read = ReadAsset;
        var resolveName = ResolveBinName ?? (_ => null);
        foreach (string name in PropAnimations.GraphClipNames(bin, path => read(path), resolveName)) Clips.Add(name);
        SelectedClip = Clips.FirstOrDefault(c => c.Equals("Idle1", StringComparison.OrdinalIgnoreCase))
                       ?? Clips.FirstOrDefault(c => c.Contains("idle", StringComparison.OrdinalIgnoreCase))
                       ?? Clips.FirstOrDefault();

        Detail = $"{System.IO.Path.GetFileName(meshPath)}  ·  {Clips.Count} named clip(s)"
               + (SelectedClip is null ? "  ·  no named idle - it will stand in its bind pose" : $"  ·  idles with {SelectedClip}")
               + $"\n{SkinObjectPath}";
        Status = $"{character.Name} / {value.Label} is ready to place.";
        OnPropertyChanged(nameof(CanAdd));
    }

    /// <summary>The bin's own object path, resolved to text. A skin bin holds one
    /// SkinCharacterDataProperties; its path hash is the string the placement must carry.</summary>
    private string? ObjectPathOf(byte[] skinBin)
    {
        if (ResolveBinName is null) return null;
        try
        {
            var tree = Formats.Meta.SafeBinTree.Parse(skinBin);
            uint skinClass = Core.Hashing.HashAlgorithms.Fnv1a("SkinCharacterDataProperties");
            foreach (var o in tree.Objects.Values)
                if (o.ClassHash == skinClass && ResolveBinName(o.PathHash) is { Length: > 0 } name)
                    return name;
        }
        catch { /* an unreadable bin falls back to the constructed path */ }
        return null;
    }

    private static string FallbackSkinPath(string character, CharacterSkinRef skin) =>
        $"Characters/{character}/Skins/Skin{Math.Max(skin.Number, 0)}";

    /// <summary>The record every scenery placement names. Riot's own are
    /// <c>Characters/&lt;Name&gt;/CharacterRecords/Root</c> - 23 of the 24 characters placed as scenery on
    /// Map11, Map12 and Map453, the exception being a second record of a character that has one.</summary>
    private string RecordPathOf(string character)
    {
        string constructed = CharacterPackageBuilder.CharacterRecordPath(character);
        return ResolveBinName?.Invoke(Core.Hashing.HashAlgorithms.Fnv1a(constructed)) is { Length: > 0 } known ? known : constructed;
    }

    [RelayCommand]
    private async Task AddProp()
    {
        if (!CanAdd || Add is null || SkinObjectPath is not { } skin || CharacterRecordPath is not { } record
            || SelectedCharacter is not { } character) return;
        Busy = true;
        try { Status = await Add(new AddPropRequest(character.Name, record, skin, SelectedClip, PlaceAsAnimatedProp,
            PlaceAsAnimatedProp ? (float)AppearAfterSeconds : 0f)); }
        catch (Exception ex) { Status = ex.Message; }
        finally { Busy = false; }
    }
}
