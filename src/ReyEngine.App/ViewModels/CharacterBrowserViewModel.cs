using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;

namespace ReyEngine.App.ViewModels;

/// <summary>What the browser needs from the editor around it: where the game is, how to name a hash,
/// and somewhere to send a skin once it has been chosen.</summary>
public interface ICharacterBrowserHost
{
    /// <summary>The League <c>Game</c> folder, or null when one has not been set.</summary>
    string? GameDirectory { get; }

    IHashResolver? Resolver { get; }

    /// <summary>Load this skin into the model preview. The browser knows nothing about rendering.</summary>
    void OpenSkin(ChampionPackage champion, CharacterEntry character, CharacterSkinInfo skin);
}

public sealed class ChampionRowViewModel(ChampionPackage package, ClientChampion? client)
{
    public ChampionPackage Package { get; } = package;
    public string Name { get; } = client?.DisplayName is { Length: > 0 } n ? n : package.Name;
    /// <summary>Riot's folder name, shown when it differs from the display name — TwistedFate vs
    /// "Twisted Fate" is the difference between finding a file and not.</summary>
    public string Folder { get; } = package.Name;
    public string Title { get; } = client?.Title ?? "";
    public bool ShowFolder => !Folder.Equals(Name, StringComparison.Ordinal);

    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Folder.Contains(query, StringComparison.OrdinalIgnoreCase);
}

public sealed class CharacterRowViewModel(CharacterEntry entry)
{
    public CharacterEntry Entry { get; } = entry;
    public string Name { get; } = entry.Name;
    public string KindText { get; } = entry.Kind switch
    {
        CharacterKind.Champion => "champion",
        CharacterKind.Jade => "arena double",
        _ => "companion",
    };
    public string SkinCountText { get; } = entry.Skins.Count(s => !s.IsRoot) + " skins";
}

public sealed class SkinRowViewModel(CharacterSkinRef reference, ClientSkin? client)
{
    public CharacterSkinRef Reference { get; } = reference;
    public int Number { get; } = reference.Number;
    public string NumberText { get; } = reference.Number.ToString();
    /// <summary>The client name when there is one. Companions and mode-only skins have none, and their
    /// code name lives in the bin - which is not read until the skin is actually selected.</summary>
    public string Name { get; } = client?.DisplayName is { Length: > 0 } n ? n : "Skin " + reference.Number;
    public string Badge { get; } = client is null ? "" : client.IsBase ? "base" : client.IsLegacy ? "legacy" : "";
}

/// <summary>
/// M610: the front door to a character.
///
/// <para>Pick a champion, pick which of its characters (Elise has three), pick a skin, open it. Before
/// this the only way in was to know a path, open the WAD it lived in and find the .skn by eye in a tree
/// of six thousand chunks.</para>
///
/// <para>Work is done in the order the user asks for it: the champion list is file names only, the
/// character and skin lists come from one archive's paths, and a skin BIN is parsed only when that skin
/// is selected. Ahri ships 96 of them, so filling a list from their contents would be seconds of work to
/// answer a question nobody asked.</para>
/// </summary>
public sealed partial class CharacterBrowserViewModel : ObservableObject, IDisposable
{
    private readonly ICharacterBrowserHost _host;
    private readonly Action<string, string>? _log;
    private ClientNameCatalog _names = ClientNameCatalog.Load(null);
    private WadArchive? _archive;
    private IReadOnlyList<ChampionRowViewModel> _allChampions = Array.Empty<ChampionRowViewModel>();

    public ObservableCollection<ChampionRowViewModel> Champions { get; } = new();
    public ObservableCollection<CharacterRowViewModel> Characters { get; } = new();
    public ObservableCollection<SkinRowViewModel> Skins { get; } = new();
    public ObservableCollection<string> Overrides { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ChampionRowViewModel? _selectedChampion;
    [ObservableProperty] private CharacterRowViewModel? _selectedCharacter;
    [ObservableProperty] private SkinRowViewModel? _selectedSkin;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;

    // ---- the selected skin, once its bin has been read --------------------------------------------
    [ObservableProperty] private string _codeName = "";
    [ObservableProperty] private string _meshPath = "";
    [ObservableProperty] private string _skeletonPath = "";
    [ObservableProperty] private string _texturePath = "";
    [ObservableProperty] private string _skinLine = "";
    [ObservableProperty] private string _hiddenSubmeshes = "";
    private CharacterSkinInfo? _skinInfo;

    public bool HasSkinDetails => _skinInfo is not null;
    public bool CanOpen => _skinInfo is { IsLoadable: true };

    public CharacterBrowserViewModel(ICharacterBrowserHost host, Action<string, string>? log = null)
    {
        _host = host;
        _log = log;
        Load();
    }

    /// <summary>Where the champion WADs live for the configured game folder, or null.</summary>
    public string? ChampionsDirectory
    {
        get
        {
            string? final = Core.Assets.GameReferenceLibrary.FindFinalDirectory(_host.GameDirectory);
            if (final is null) return null;
            string champions = Path.Combine(final, "Champions");
            return Directory.Exists(champions) ? champions : null;
        }
    }

    private void Load()
    {
        if (ChampionsDirectory is not { } directory)
        {
            Status = "No game folder is set. Project > Set Game Folder..., then reopen this window.";
            return;
        }

        var packages = CharacterCatalog.Champions(directory);

        // The marketing names are enrichment, never a dependency: without the client plugin the list is
        // exactly as complete, just spelled the way the folders are.
        string? installRoot = ClientNameCatalog.InstallRootFromChampions(directory);
        _names = ClientNameCatalog.Load(installRoot is null ? null : ClientNameCatalog.FindDataWad(installRoot));

        _allChampions = packages.Select(p => new ChampionRowViewModel(p, _names.Champion(p.Name))).ToList();
        ApplyFilter();
        Status = _names.ChampionCount > 0
            ? $"{packages.Count} champions."
            : $"{packages.Count} champions. Client names unavailable - showing folder names.";
    }

    partial void OnSearchChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var previous = SelectedChampion;
        Champions.Clear();
        string query = Search.Trim();
        foreach (var champion in _allChampions)
            if (query.Length == 0 || champion.Matches(query))
                Champions.Add(champion);

        // Typing must not throw away a selection that still matches - the skin list below it would clear.
        if (previous is not null && Champions.Contains(previous)) SelectedChampion = previous;
    }

    partial void OnSelectedChampionChanged(ChampionRowViewModel? value)
    {
        Characters.Clear();
        Skins.Clear();
        ClearSkinDetails();
        _archive?.Dispose();
        _archive = null;
        if (value is null) return;

        try
        {
            Busy = true;
            _archive = WadArchive.Open(value.Package.WadPath, _host.Resolver);

            // A WAD whose chunks are still 0x-named lists nothing, and "no characters" would be the only
            // symptom. Say which of the two it is instead.
            if (_archive.ResolvedCount == 0)
            {
                Status = $"{value.Name}: no hash dictionary loaded, so nothing in this WAD can be named.";
                return;
            }

            foreach (var character in CharacterCatalog.Characters(_archive, value.Package.Name))
                Characters.Add(new CharacterRowViewModel(character));

            SelectedCharacter = Characters.FirstOrDefault();
            Status = $"{value.Name}: {Characters.Count} character(s) in {Path.GetFileName(value.Package.WadPath)}.";
        }
        catch (Exception ex)
        {
            Status = $"{value.Name}: {ex.Message}";
            _log?.Invoke("Character", $"{value.Package.WadPath}: {ex.Message}");
        }
        finally
        {
            Busy = false;
        }
    }

    partial void OnSelectedCharacterChanged(CharacterRowViewModel? value)
    {
        Skins.Clear();
        ClearSkinDetails();
        if (value is null || SelectedChampion is null) return;

        // root.bin is the champion's shared defaults, not a skin: it has no mesh, so offering it would be
        // offering an empty viewport.
        foreach (var reference in value.Entry.Skins.Where(s => !s.IsRoot))
            Skins.Add(new SkinRowViewModel(reference, _names.Skin(value.Entry.Name, reference.Number)));

        SelectedSkin = Skins.FirstOrDefault();
    }

    partial void OnSelectedSkinChanged(SkinRowViewModel? value)
    {
        ClearSkinDetails();
        if (value is null || _archive is null) return;

        try
        {
            ulong hash = HashAlgorithms.WadPath(value.Reference.BinPath);
            if (!_archive.TryGetEntry(hash, out _)) { Status = $"{value.Reference.BinPath} is not in this WAD."; return; }

            var archive = _archive;
            _skinInfo = CharacterSkinReader.Read(archive.Extract(hash), value.Reference.BinPath,
                h => archive.TryGetEntry(h, out var e) && e.IsResolved ? e.Path : null);
            if (_skinInfo is null) { Status = $"{value.Reference.BinPath} is not a skin."; return; }

            CodeName = _skinInfo.CodeName;
            MeshPath = _skinInfo.MeshPath ?? "(none)";
            SkeletonPath = _skinInfo.SkeletonPath ?? "(none)";
            TexturePath = _skinInfo.TexturePath ?? "(none)";
            SkinLine = _skinInfo.SkinLine ?? "";
            HiddenSubmeshes = _skinInfo.InitiallyHiddenSubmeshes.Count == 0
                ? ""
                : string.Join(", ", _skinInfo.InitiallyHiddenSubmeshes);
            foreach (var over in _skinInfo.MaterialOverrides)
                Overrides.Add($"{over.Submesh}  ->  {over.TexturePath ?? over.Material ?? "(material only)"}");

            Status = _skinInfo.IsLoadable
                ? $"{value.Name} ({_skinInfo.CodeName})"
                : $"{value.Name} has no mesh of its own - nothing to open.";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            OnPropertyChanged(nameof(HasSkinDetails));
            OnPropertyChanged(nameof(CanOpen));
            OpenSkinCommand.NotifyCanExecuteChanged();
        }
    }

    private void ClearSkinDetails()
    {
        _skinInfo = null;
        CodeName = MeshPath = SkeletonPath = TexturePath = SkinLine = HiddenSubmeshes = "";
        Overrides.Clear();
        OnPropertyChanged(nameof(HasSkinDetails));
        OnPropertyChanged(nameof(CanOpen));
        OpenSkinCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void OpenSkin()
    {
        if (SelectedChampion is not { } champion || SelectedCharacter is not { } character
            || _skinInfo is not { IsLoadable: true } skin) return;

        _host.OpenSkin(champion.Package, character.Entry, skin);
        Status = $"Opening {SelectedSkin?.Name} ({skin.CodeName})...";
    }

    public void Dispose()
    {
        _archive?.Dispose();
        _archive = null;
    }
}
