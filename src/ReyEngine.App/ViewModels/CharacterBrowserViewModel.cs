using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
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
    IReadOnlyList<string> ProjectCharacterPaths => Array.Empty<string>();
    byte[]? ReadProjectCharacterAsset(string path) => null;

    /// <summary>Load this skin into the model preview. The browser knows nothing about rendering.</summary>
    void OpenSkin(ChampionPackage champion, CharacterEntry character, CharacterSkinInfo skin);
}

public sealed class ChampionRowViewModel(ChampionPackage package, ClientChampion? client, bool inProject = false,
    ClientImageService? images = null)
{
    public ChampionPackage Package { get; } = package;
    public string Name { get; } = client?.DisplayName is { Length: > 0 } n ? n : package.Name;
    /// <summary>Riot's folder name, shown when it differs from the display name — TwistedFate vs
    /// "Twisted Fate" is the difference between finding a file and not.</summary>
    public string Folder { get; } = package.Name;
    public string Title { get; } = client?.Title ?? "";
    public string SourceLabel { get; } = inProject ? "PROJECT" : "RIOT REFERENCE";
    public bool ShowFolder => !Folder.Equals(Name, StringComparison.Ordinal);
    public bool InProject { get; } = inProject;

    /// <summary>M870: the tile's tooltip: the name, the title when the client has one, the folder when it differs, and where it comes from.</summary>
    public string Tooltip { get; } = (client?.DisplayName is { Length: > 0 } dn ? dn : package.Name)
        + (client?.Title is { Length: > 0 } tt ? " - " + tt : "")
        + (client?.DisplayName is { Length: > 0 } dn2 && !dn2.Equals(package.Name, StringComparison.Ordinal) ? "\n" + package.Name : "")
        + (inProject ? "\nIn the project" : "");

    /// <summary>M870: the client's champion id, or 0 when the client does not know this character (a project character, a
    /// client-less install). Skin ids are this times 1000 plus the skin number.</summary>
    public int ClientId { get; } = client?.Id ?? 0;
    public IReadOnlyList<string> Roles { get; } = client?.Roles ?? Array.Empty<string>();

    /// <summary>M870: the square portrait. Decoded only when its tile is on screen; a character the client does not know has none and
    /// the tile shows <see cref="Initial"/>.</summary>
    public ClientImageHandle Icon { get; } = client is null
        ? ClientImageHandle.None
        : new ClientImageHandle(images, ClientChampionData.IconPathOf(client.Id), 112);

    public string Initial { get; } = (client?.DisplayName is { Length: > 0 } d ? d : package.Name) is { Length: > 0 } t
        ? t[..1].ToUpperInvariant() : "?";

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

public sealed class SkinRowViewModel(CharacterSkinRef reference, ClientSkin? client, ClientImageService? images = null) : ObservableObject
{
    public CharacterSkinRef Reference { get; } = reference;
    public int Number { get; } = reference.Number;
    public string NumberText { get; } = reference.Number.ToString();
    /// <summary>The client name when there is one. Companions and mode-only skins have none, and their
    /// code name lives in the bin - which is not read until the skin is actually selected.</summary>
    public string Name { get; } = client?.DisplayName is { Length: > 0 } n ? n : "Skin " + reference.Number;
    /// <summary>M728: "chroma" before the rest - a chroma is neither base nor legacy, and without it one listed as a
    /// bare "Skin 49" beside the skin it recolours.</summary>
    public string Badge { get; } = client is null ? ""
        : client.IsChroma ? "chroma"
        : client.IsBase ? "base"
        : client.IsLegacy ? "legacy"
        : "";

    // ---- M870: the card ----------------------------------------------------------------------------

    /// <summary>The number of the skin this one recolours, or null for a skin. A chroma has no card of its own: it is a swatch under its skin.</summary>
    public int? ChromaOf { get; } = client?.ChromaOf;
    public bool IsChroma => ChromaOf is not null;

    /// <summary>The client's rarity word ("Epic", "Legendary"), or null.</summary>
    public string? Rarity { get; } = client?.Rarity;

    /// <summary>What the card's chip says: the rarity, else base / legacy, else nothing.</summary>
    public string ChipText { get; } = client?.Rarity is { Length: > 0 } r ? r
        : client is { IsBase: true } ? "Base"
        : client is { IsLegacy: true } ? "Legacy"
        : "";
    public bool HasChip => ChipText.Length > 0;
    /// <summary>Legendary and above get the accent chip.</summary>
    public bool IsPremium { get; } = client?.Rarity is "Legendary" or "Ultimate" or "Mythic" or "Transcendent";

    /// <summary>Set by the browser: select this skin and load it (the card's click, the swatch's click).</summary>
    public System.Windows.Input.ICommand? Select { get; set; }

    public ClientImageHandle Tile { get; } = images is not null && client?.TilePath is { Length: > 0 } tile
        ? new ClientImageHandle(images, tile, 180)
        : ClientImageHandle.None;

    /// <summary>The loading-screen art of the skin (a chroma has none of its own; the header uses its skin's).</summary>
    public string? LoadScreenPath { get; } = client?.LoadScreenPath;

    private bool _isActive;
    /// <summary>True for the card of the selected skin - and of the skin whose chroma is selected.</summary>
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
}

/// <summary>M870: one chroma of the selected skin as a round colour swatch. Only chromas whose bin exists in the champion WAD get one.</summary>
public sealed class ChromaSwatchViewModel : ObservableObject
{
    public ChromaSwatchViewModel(SkinRowViewModel row, ClientChroma? chroma, ClientImageService? images)
    {
        Row = row;
        Name = chroma?.Name is { Length: > 0 } n ? n : row.Name;
        Colors = chroma?.Colors ?? Array.Empty<string>();
        Fill = MakeFill(Colors);
        Picture = chroma?.ImagePath is { Length: > 0 } p && images is not null ? new ClientImageHandle(images, p, 220) : ClientImageHandle.None;
    }

    public SkinRowViewModel Row { get; }
    public string Name { get; }
    public IReadOnlyList<string> Colors { get; }
    public IBrush Fill { get; }
    public ClientImageHandle Picture { get; }
    public bool HasPicture => Picture.CanLoad;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>One colour is a disc; two are a disc split along the diagonal. No colour at all (the client lists none) is a neutral disc.</summary>
    internal static IBrush MakeFill(IReadOnlyList<string> colors)
    {
        var parsed = new List<Color>();
        foreach (string c in colors)
            if (Color.TryParse(c, out var color)) parsed.Add(color);
        if (parsed.Count == 0) return Brushes.Gray;
        if (parsed.Count == 1 || parsed[0] == parsed[1]) return new SolidColorBrush(parsed[0]);
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(parsed[0], 0.0), new GradientStop(parsed[0], 0.5),
                new GradientStop(parsed[1], 0.5), new GradientStop(parsed[1], 1.0),
            },
        };
    }
}

/// <summary>M870: the banner above the right panel: the loading-screen art of the skin shown, and who it is.</summary>
public sealed class ChampionHeaderViewModel : ObservableObject
{
    private ClientImageHandle _art = ClientImageHandle.None;
    private string _name = "", _title = "", _roles = "", _skinName = "";
    private bool _hasChampion;

    public ClientImageHandle Art { get => _art; private set => SetProperty(ref _art, value); }
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    /// <summary>"Marksman" or "Fighter · Assassin".</summary>
    public string Roles { get => _roles; private set => SetProperty(ref _roles, value); }
    public string SkinName { get => _skinName; private set => SetProperty(ref _skinName, value); }
    public bool HasChampion { get => _hasChampion; private set => SetProperty(ref _hasChampion, value); }
    public bool HasTitle => _title.Length > 0;
    public bool HasRoles => _roles.Length > 0;

    internal void Apply(ChampionRowViewModel? champion, SkinRowViewModel? skin, string? loadScreen, ClientImageService? images)
    {
        if (champion is null)
        {
            Name = Title = Roles = SkinName = "";
            Art = ClientImageHandle.None;
            HasChampion = false;
        }
        else
        {
            Name = champion.Name;
            Title = champion.Title;
            Roles = string.Join(" · ", champion.Roles.Select(r => r.Length == 0 ? r : char.ToUpperInvariant(r[0]) + r[1..]));
            SkinName = skin?.Name ?? "";
            Art = images is not null && loadScreen is { Length: > 0 } ? new ClientImageHandle(images, loadScreen, 160) : ClientImageHandle.None;
            HasChampion = true;
        }
        OnPropertyChanged(nameof(HasTitle));
        OnPropertyChanged(nameof(HasRoles));
    }
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
    private ClientChampionData _client = ClientChampionData.Unavailable();
    private int? _chromaParent;
    private WadArchive? _archive;
    private IReadOnlyList<ChampionRowViewModel> _allChampions = Array.Empty<ChampionRowViewModel>();

    public ObservableCollection<ChampionRowViewModel> Champions { get; } = new();
    public ObservableCollection<CharacterRowViewModel> Characters { get; } = new();
    public ObservableCollection<SkinRowViewModel> Skins { get; } = new();
    public ObservableCollection<string> Overrides { get; } = new();

    // ---- M870: the card UI. The lists above stay the model; these are the picker's view of them ----
    /// <summary>The selected character's skins that have a card: every skin whose bin exists, chromas left out (they are swatches).</summary>
    public ObservableCollection<SkinRowViewModel> SkinCards { get; } = new();
    /// <summary>The chromas of the selected skin whose bin exists in the champion WAD.</summary>
    public ObservableCollection<ChromaSwatchViewModel> Chromas { get; } = new();
    /// <summary>The banner above the right panel: the skin last opened.</summary>
    public ChampionHeaderViewModel Header { get; } = new();
    /// <summary>The League client's metadata and art for this install. Never null; empty without a client.</summary>
    public ClientChampionData ClientData => _client;
    /// <summary>Decodes the client's images on demand (cached, bounded).</summary>
    public ClientImageService Images { get; } = new(ClientChampionData.Unavailable());
    public bool HasCompanions => Characters.Count > 1;
    public bool HasSkinCards => SkinCards.Count > 0;
    public bool HasChromas => Chromas.Count > 0;

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

    /// <summary>M812: the client's name for one skin of a character - "Petals of Spring Lillia (Rose Quartz)" - or null
    /// when the client data does not have it (a Game-only install, a companion, a mode-only skin).</summary>
    public string? SkinDisplayName(string character, int number) => _names.Skin(character, number)?.DisplayName;

    public bool HasSkinDetails => _skinInfo is not null;
    public bool CanOpen => _skinInfo is { IsLoadable: true };

    public CharacterBrowserViewModel(ICharacterBrowserHost host, Action<string, string>? log = null)
    {
        _host = host;
        _log = log;
        Load();
    }

    /// <summary>M643: list the install again. The picker lives inside the character window now and outlives
    /// a change of game folder, so it has to be able to start over.</summary>
    public void Reload()
    {
        // The window outlives a reload (a project change, the menu command): the champion, character and skin the user was on come
        // back, so the SKINS card does not vanish under a skin that is loading.
        string? folder = SelectedChampion?.Folder, character = SelectedCharacter?.Name;
        int? skin = SelectedSkin?.Number;
        Champions.Clear();
        Characters.Clear();
        Skins.Clear();
        ClearCards();
        SelectedChampion = null;
        _allChampions = Array.Empty<ChampionRowViewModel>();
        Load();
        if (folder is null) return;
        var again = Champions.FirstOrDefault(c => c.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
        if (again is null) return;
        SelectedChampion = again;
        if (character is not null && Characters.FirstOrDefault(c => c.Name.Equals(character, StringComparison.OrdinalIgnoreCase)) is { } c2)
            SelectedCharacter = c2;
        if (skin is int number && Skins.FirstOrDefault(sk => sk.Number == number) is { } s2) SelectedSkin = s2;
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
        string? directory = ChampionsDirectory;
        var projectPaths = _host.ProjectCharacterPaths;
        if (directory is null && projectPaths.Count == 0)
        {
            Status = "No game folder is set. Project > Set Game Folder..., then reopen this window.";
            return;
        }

        var packages = (directory is null ? Array.Empty<ChampionPackage>() : CharacterCatalog.Champions(directory)).ToList();
        var projectCharacters = CharacterCatalog.Characters(projectPaths, null);
        foreach (var character in projectCharacters)
            if (!packages.Any(p => p.Name.Equals(character.Name, StringComparison.OrdinalIgnoreCase)))
                packages.Add(new ChampionPackage(character.Name, ""));

        // The marketing names are enrichment, never a dependency: without the client plugin the list is
        // exactly as complete, just spelled the way the folders are.
        string? installRoot = directory is null ? null : ClientNameCatalog.InstallRootFromChampions(directory);
        string? dataWad = installRoot is null ? null : ClientNameCatalog.FindDataWad(installRoot);
        _names = ClientNameCatalog.Load(dataWad);

        // M870: the same plugin WADs, kept open for the portraits, tiles and ability icons. Lazy: nothing is read until a tile is shown.
        var previous = _client;
        _client = ClientChampionData.Open(dataWad);
        Images.Use(_client);                   // the same service: rows built earlier keep working after a reload
        OnPropertyChanged(nameof(ClientData));
        previous.Dispose();
        _ = _client.PrefetchAsync();           // the WAD open and the summary parse happen on a worker, not on the first click

        // The grid is sorted by what it shows: the display name ("Twisted Fate"), not the folder ("TwistedFate").
        _allChampions = packages
            .Select(p => new ChampionRowViewModel(p, _names.Champion(p.Name),
                projectCharacters.Any(c => c.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)), Images))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Folder, StringComparer.OrdinalIgnoreCase).ToList();
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
        ClearCards();
        ClearSkinDetails();
        _archive?.Dispose();
        _archive = null;
        if (value is null) return;

        try
        {
            Busy = true;
            if (!string.IsNullOrEmpty(value.Package.WadPath))
                _archive = WadArchive.Open(value.Package.WadPath, _host.Resolver);

            // A WAD whose chunks are still 0x-named lists nothing, and "no characters" would be the only
            // symptom. Say which of the two it is instead.
            if (_archive is not null && _archive.ResolvedCount == 0)
            {
                Status = $"{value.Name}: no hash dictionary loaded, so nothing in this WAD can be named.";
                return;
            }

            var paths = _archive?.Entries.Where(e => e.IsResolved).Select(e => e.Path).ToList() ?? new List<string>();
            var names = CharacterCatalog.Characters(paths, value.Package.Name).Select(c => c.Name)
                .Append(value.Package.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            paths.AddRange(_host.ProjectCharacterPaths.Where(p => p.Split('/').Length > 2 && names.Contains(p.Split('/')[2])));
            foreach (var character in CharacterCatalog.Characters(paths, value.Package.Name))
                Characters.Add(new CharacterRowViewModel(character));

            OnPropertyChanged(nameof(HasCompanions));
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
        ClearCards();
        ClearSkinDetails();
        if (value is null || SelectedChampion is null) return;

        // root.bin is the champion's shared defaults, not a skin: it has no mesh, so offering it would be
        // offering an empty viewport.
        foreach (var reference in value.Entry.Skins.Where(s => !s.IsRoot))
            Skins.Add(new SkinRowViewModel(reference, _names.Skin(value.Entry.Name, reference.Number), Images) { Select = SelectSkinCommand });
        BuildCards();

        SelectedSkin = Skins.FirstOrDefault();
    }

    partial void OnSelectedSkinChanged(SkinRowViewModel? value)
    {
        ClearSkinDetails();
        RefreshSelection(value);
        if (value is null) return;

        try
        {
            ulong hash = HashAlgorithms.WadPath(value.Reference.BinPath);
            var projectBytes = _host.ReadProjectCharacterAsset(value.Reference.BinPath);
            if (projectBytes is null && (_archive is null || !_archive.TryGetEntry(hash, out _)))
            { Status = $"{value.Reference.BinPath} is not available."; return; }

            var archive = _archive;
            _skinInfo = CharacterSkinReader.Read(projectBytes ?? archive!.Extract(hash), value.Reference.BinPath,
                h => _host.Resolver is { } resolver && resolver.TryGetPath(h, out var resolved) ? resolved
                    : (archive is not null && archive.TryGetEntry(h, out var e) && e.IsResolved ? e.Path : null));
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

    // ---- M870: cards, swatches, banner ---------------------------------------------------------------

    private void ClearCards()
    {
        SkinCards.Clear();
        Chromas.Clear();
        _chromaParent = null;
        OnPropertyChanged(nameof(HasSkinCards));
        OnPropertyChanged(nameof(HasChromas));
        OnPropertyChanged(nameof(HasCompanions));
    }

    /// <summary>One card per skin the character really has a bin for, base first and then by number. A chroma is a swatch, not a card;
    /// every row here came from the champion WAD's own skinN.bin list, so no card is a dead entry.</summary>
    private void BuildCards()
    {
        SkinCards.Clear();
        foreach (var row in Skins.Where(s => !s.IsChroma).OrderBy(s => s.Number)) SkinCards.Add(row);
        OnPropertyChanged(nameof(HasSkinCards));
    }

    /// <summary>The chromas build that is running or last ran; a test awaits it.</summary>
    public Task ChromasTask { get; private set; } = Task.CompletedTask;

    /// <summary>Mark the card of the selected skin (or of the skin whose chroma it is) and show that skin's chromas. The client's colours
    /// come from a JSON the first selection of a champion has to read, so that happens on a worker and the swatches appear when it is done.</summary>
    private void RefreshSelection(SkinRowViewModel? selected)
    {
        int? parent = selected is null ? null : selected.ChromaOf ?? selected.Number;
        foreach (var card in SkinCards) card.IsActive = parent is not null && card.Number == parent;

        if (_chromaParent != parent)
        {
            Chromas.Clear();
            _chromaParent = parent;
            OnPropertyChanged(nameof(HasChromas));
            // The client lists chromas under the champion's main character; a companion's skins have none to offer.
            if (parent is int number && SelectedChampion is { ClientId: > 0 } champion
                && SelectedCharacter is { } character && character.Name.Equals(champion.Folder, StringComparison.OrdinalIgnoreCase)
                && Skins.Any(sk => sk.ChromaOf == number))
                ChromasTask = BuildChromasAsync(number, champion.ClientId, champion);
        }
        foreach (var swatch in Chromas) swatch.IsSelected = ReferenceEquals(swatch.Row, selected);
    }

    private async Task BuildChromasAsync(int number, int clientId, ChampionRowViewModel champion)
    {
        var client = _client;
        ClientChampionDetails? details = null;
        try { details = await client.GetDetailsAsync(clientId); } catch { /* no colours: neutral discs */ }
        // the user moved on while the JSON was read: this answer is for a skin that is no longer shown
        if (_chromaParent != number || !ReferenceEquals(SelectedChampion, champion)) return;
        var detail = details?.Skin(number);
        Chromas.Clear();
        foreach (var row in Skins.Where(sk => sk.ChromaOf == number).OrderBy(sk => sk.Number))
            Chromas.Add(new ChromaSwatchViewModel(row, detail?.Chromas.FirstOrDefault(c => c.Number == row.Number), Images));
        foreach (var swatch in Chromas) swatch.IsSelected = ReferenceEquals(swatch.Row, SelectedSkin);
        OnPropertyChanged(nameof(HasChromas));
    }

    private (ChampionRowViewModel Champion, SkinRowViewModel Skin, string? LoadScreen, string BinPath)? _pendingLoad;

    /// <summary>The host calls this when a skin has really finished loading into the window (not when it was merely asked for): the banner
    /// then shows the champion and skin that are ON STAGE. If what loaded is not what this browser asked for (a skin opened from the asset
    /// tree, a prop), the banner is cleared rather than left naming a champion that is no longer shown. A load that fails never calls
    /// this, so the banner keeps describing what is still on stage.</summary>
    public void NotifySkinLoaded(string? binPath)
    {
        static string Norm(string? p) => (p ?? "").Replace('\\', '/').ToLowerInvariant();
        if (_pendingLoad is { } pending && binPath is not null && Norm(pending.BinPath) == Norm(binPath))
        {
            Header.Apply(pending.Champion, pending.Skin, pending.LoadScreen, Images);
            _pendingLoad = null;
        }
        else
        {
            _pendingLoad = null;
            Header.Apply(null, null, null, Images);
        }
    }

    private void RememberLoad()
    {
        var skin = SelectedSkin;
        if (SelectedChampion is not { } champion || skin is null) { _pendingLoad = null; return; }
        var card = skin.IsChroma ? SkinCards.FirstOrDefault(c => c.Number == skin.ChromaOf) ?? skin : skin;
        _pendingLoad = (champion, skin, card.LoadScreenPath, skin.Reference.BinPath);
    }

    /// <summary>A click on a skin card or a chroma swatch: select that skin and load it. The Load button is gone from the window; the
    /// load path is the one <see cref="OpenSkinCommand"/> always used.</summary>
    [RelayCommand]
    private void SelectSkin(SkinRowViewModel? row)
    {
        if (row is null) return;
        if (!ReferenceEquals(SelectedSkin, row)) SelectedSkin = row;
        else OnSelectedSkinChanged(row);          // same skin again: read its bin afresh, then load it again
        if (CanOpen) OpenSkin();
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

        RememberLoad();
        _host.OpenSkin(champion.Package, character.Entry, skin);
        Status = $"Opening {SelectedSkin?.Name} ({skin.CodeName})...";
    }

    public void Dispose()
    {
        _archive?.Dispose();
        _archive = null;
        _client.Dispose();
    }
}
