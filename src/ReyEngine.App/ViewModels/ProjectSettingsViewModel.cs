using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Projects;

namespace ReyEngine.App.ViewModels;

/// <summary>Backs the Project Settings dialog: .fantome mod metadata + game/output folders.</summary>
public sealed partial class ProjectSettingsViewModel : ViewModelBase
{
    private readonly DialogService _dialogs;

    [ObservableProperty] private string _modName = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _version = "1.0.0";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _heart = "";
    [ObservableProperty] private string _home = "";
    /// <summary>M470: the LTK Manager workshop mod this project sends to. Empty = derive it from
    /// the mod name on first send. Set it to adopt an existing workshop mod whose slug does not
    /// match the name (the user's own "Old Summoner's Rift - Day" lives in a folder called
    /// oldriftday, which no slugifier would produce).</summary>
    [ObservableProperty] private string _ltkWorkshopSlug = "";
    [ObservableProperty] private bool _shipBinEditsAsDeclarations;   // M757
    [ObservableProperty] private string _thumbnailPath = "";
    [ObservableProperty] private string _gameDirectory = "";
    [ObservableProperty] private bool _hasGameDirectoryError;
    [ObservableProperty] private string _gameDirectoryMessage = "";
    [ObservableProperty] private string _outputDirectory = "";
    [ObservableProperty] private bool _packKnownTypesOnly = true;   // M132
    [ObservableProperty] private string _riotPatchVersion = "";
    [ObservableProperty] private bool _autoUpdateOnRiotPatch = true;
    [ObservableProperty] private bool _autoBuildAfterPatchUpdate = true;

    /// <summary>M744: the project's modpkg layers, base first. Shared with every folder row, so a layer
    /// added or renamed here is offered there immediately.</summary>
    public ObservableCollection<ProjectLayerRow> Layers { get; } = new();

    /// <summary>Every WAD folder the project ships, and which layer it rides.</summary>
    public ObservableCollection<FolderLayerRow> FolderLayers { get; } = new();

    [ObservableProperty] private string _layerError = "";
    public bool HasLayerError => LayerError.Length > 0;
    partial void OnLayerErrorChanged(string value) => OnPropertyChanged(nameof(HasLayerError));

    public bool Saved { get; private set; }
    public event Action? CloseRequested;

    public ProjectSettingsViewModel(ReyProject p, DialogService dialogs)
    {
        _dialogs = dialogs;
        _modName = p.EffectiveModName;
        _author = p.ModAuthor ?? "";
        _version = string.IsNullOrWhiteSpace(p.ModVersion) ? "1.0.0" : p.ModVersion;
        _description = p.ModDescription ?? "";
        _heart = p.ModHeart ?? "";
        _home = p.ModHome ?? "";
        _ltkWorkshopSlug = p.LtkWorkshopSlug ?? "";
        _shipBinEditsAsDeclarations = p.ShipBinEditsAsDeclarations;
        _thumbnailPath = p.ThumbnailPath ?? "";
        _gameDirectory = p.GameDirectory ?? "";
        _outputDirectory = p.OutputDirectory ?? "";
        _packKnownTypesOnly = p.PackKnownTypesOnly;
        // Both sides added to this constructor: main validates the game directory, M308 seeds the patch
        // fields. Neither supersedes the other.
        _riotPatchVersion = p.RiotPatchVersion ?? "";
        _autoUpdateOnRiotPatch = p.AutoUpdateOnRiotPatch;
        _autoBuildAfterPatchUpdate = p.AutoBuildAfterPatchUpdate;
        ValidateGameDirectory();
        SeedLayers(p);
    }

    partial void OnGameDirectoryChanged(string value) => ValidateGameDirectory();

    private GameReferenceStatus ValidateGameDirectory()
    {
        var status = GameReferenceLibrary.Inspect(GameDirectory);
        HasGameDirectoryError = !status.IsValid;
        GameDirectoryMessage = status.IsValid
            ? $"Verified: {status.GameDirectory}"
            : status.Message + " Select the League of Legends\\Game folder containing DATA\\FINAL.";
        return status;
    }

    // ===================================================== M744: content layers

    /// <summary>The project's layers as rows, plus the base row every project has, and one row per WAD
    /// folder showing the layer it ships in.</summary>
    private void SeedLayers(ReyProject p)
    {
        Layers.Add(new ProjectLayerRow
        {
            IsBase = true,
            Name = ProjectLayer.BaseLayer,
            Description = "Everything no other layer claims. Always shipped.",
        });
        foreach (var l in p.Layers)
        {
            if (string.Equals(l.Name, ProjectLayer.BaseLayer, StringComparison.OrdinalIgnoreCase)) continue;
            Layers.Add(new ProjectLayerRow { Name = l.Name, Priority = l.Priority, Description = l.Description, Source = l });
        }

        // The exporter ships a folder under the leaf of its resolved path, so that is the name a layer
        // claims - not the possibly-relative entry in ProjectFolders.
        // M816: except a folder a layer claims WHOLE (an imported layer's WAD, layers/<layer>/<Wad>): its leaf is also the leaf of the
        // base folder of the same WAD, so it is listed, and written back, by its whole entry.
        foreach (string entry in p.ProjectFolders)
        {
            string folder = p.IsClaimedWhole(entry)
                ? ReyProject.NormalizeFolderEntry(entry)
                : Path.GetFileName(p.ResolveProjectPath(entry).TrimEnd('/', '\\'));
            if (folder.Length == 0
                || FolderLayers.Any(r => string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase)))
                continue;
            string owner = p.LayerOfFolder(entry);
            var row = Layers.FirstOrDefault(l => string.Equals(l.Name, owner, StringComparison.OrdinalIgnoreCase))
                      ?? Layers[0];
            FolderLayers.Add(new FolderLayerRow(folder, row, Layers));
        }
    }

    public bool HasFolders => FolderLayers.Count > 0;

    [RelayCommand]
    private void AddLayer()
    {
        int n = Layers.Count;
        string name = "layer" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        while (Layers.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = "layer" + (++n).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Above every existing layer, so a new one is applied over what is already there.
        int priority = Layers.Count == 0 ? 10 : Layers.Max(l => l.Priority) + 10;
        Layers.Add(new ProjectLayerRow { Name = name, Priority = priority });
        LayerError = "";
    }

    /// <summary>Delete a layer. Its folders fall back to base rather than vanishing with it - a folder
    /// always ships somewhere.
    ///
    /// <para>M816 review: except what an imported .fantome gave the layer, which has nowhere to fall back to. A WAD folder it claims whole
    /// cannot ship in base (its leaf is the name of a WAD base has too). And its GameData and string overrides belong to the LAYER: Save
    /// rebuilds the layer list from the rows, so a removed row takes its declarations key and its overrides with it, an export and a send
    /// leave them out, and the stored files stay behind unused. Removal is refused for each, with the reason (review, round 3).</para></summary>
    [RelayCommand]
    private void RemoveLayer(ProjectLayerRow? row)
    {
        if (row is null || row.IsBase || !Layers.Contains(row)) return;
        var reasons = new List<string>();
        var held = FolderLayers.Where(f => ReferenceEquals(f.Layer, row) && f.IsWholeClaim).Select(f => f.Folder).ToList();
        if (held.Count > 0)
            reasons.Add($"Layer '{row.Label}' holds the WAD folder {string.Join(", ", held.Select(h => $"'{h}'"))} of an imported .fantome, which cannot ship in base. "
                      + "Move it to another layer first.");
        bool gameData = !string.IsNullOrWhiteSpace(row.Source?.DeclarationsKey), strings = row.Source?.StringOverrides is { Count: > 0 };
        if (gameData || strings)
            reasons.Add($"Layer '{row.Label}' holds {(gameData && strings ? "GameData and string overrides" : gameData ? "GameData" : "string overrides")} "
                      + "from an imported .fantome, and removing the layer would drop "
                      + (gameData ? "its declarations (and the files that go with them)" : "its string overrides")
                      + " from every export and send. It can only be removed by editing project.json.");
        if (reasons.Count > 0)
        {
            LayerError = string.Join(" ", reasons);
            return;
        }
        foreach (var f in FolderLayers.Where(f => ReferenceEquals(f.Layer, row)))
            f.Layer = Layers[0];
        Layers.Remove(row);
        LayerError = "";
    }

    /// <summary>The first thing wrong with the layer list, or null. A name becomes a folder under
    /// <c>content/</c> in the sent mod, so it has to be usable as one and has to be unique.
    ///
    /// <para>M816 review: and the folders. A WAD folder an imported layer claims whole (its entry has a path in it) stays in a layer - in base
    /// its leaf is the name of the base WAD it sits beside - and no layer holds two folders that would ship as one WAD.</para></summary>
    private string? ValidateLayers()
    {
        foreach (var l in Layers)
            if (l.Problem(Layers) is { } problem) return problem;

        foreach (var f in FolderLayers)
            if (f.IsWholeClaim && f.Layer.IsBase)
                return $"The WAD folder '{f.Folder}' came from a layer of an imported .fantome and cannot ship in base, where it would sit beside the WAD of the same name. Move it to a layer.";

        foreach (var group in FolderLayers.GroupBy(f => (Layer: f.Layer, Wad: ReyEngine.Core.Build.FantomeLayers.WadFileName(f.Leaf).ToLowerInvariant())))
        {
            var rows = group.ToList();
            if (rows.Count > 1 && rows.Any(r => r.IsWholeClaim))
                return $"The folders {string.Join(" and ", rows.Select(r => $"'{r.Folder}'"))} would both ship as {ReyEngine.Core.Build.FantomeLayers.WadFileName(rows[0].Leaf)} "
                     + $"in layer '{group.Key.Layer.Label}'. A WAD has one folder in a layer: move one of them to another layer.";
        }
        return null;
    }

    public void ApplyTo(ReyProject p)
    {
        p.ModName = string.IsNullOrWhiteSpace(ModName) ? null : ModName.Trim();
        p.ModAuthor = string.IsNullOrWhiteSpace(Author) ? null : Author.Trim();
        p.ModVersion = string.IsNullOrWhiteSpace(Version) ? "1.0.0" : Version.Trim();
        p.ModDescription = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        p.ModHeart = string.IsNullOrWhiteSpace(Heart) ? null : Heart.Trim();
        p.ModHome = string.IsNullOrWhiteSpace(Home) ? null : Home.Trim();
        p.LtkWorkshopSlug = string.IsNullOrWhiteSpace(LtkWorkshopSlug) ? null : LtkWorkshopSlug.Trim();
        p.ShipBinEditsAsDeclarations = ShipBinEditsAsDeclarations;
        p.ThumbnailPath = string.IsNullOrWhiteSpace(ThumbnailPath) ? null : ThumbnailPath.Trim();
        var gameStatus = GameReferenceLibrary.Inspect(GameDirectory);
        if (gameStatus.IsValid) p.GameDirectory = gameStatus.GameDirectory;
        else if (string.IsNullOrWhiteSpace(GameDirectory)) p.GameDirectory = null;
        if (!string.IsNullOrWhiteSpace(OutputDirectory)) p.OutputDirectory = OutputDirectory.Trim();
        p.PackKnownTypesOnly = PackKnownTypesOnly;
        p.RiotPatchVersion = RiotPatchVersionDetector.TryNormalize(RiotPatchVersion, out var patch) ? patch : null;
        p.AutoUpdateOnRiotPatch = AutoUpdateOnRiotPatch;
        p.AutoBuildAfterPatchUpdate = AutoBuildAfterPatchUpdate;

        // M744: layers, rebuilt from the rows. A layer with no folders is kept - the user may be setting
        // one up before moving content into it, and dropping it would silently discard their typing.
        // M816: and the base layer an import gave something to keep (its GameData, display name, string overrides) is not a row of its own
        // here - the dialog holds base as a fixed row - so it is carried over as it was, ahead of the others.
        // Review (round 3): carried over WITHOUT its Folders. Base is the layer a folder no other layer claims falls back to, and the dialog
        // never wrote a folder into it; but ReyProject.LayerOf takes the first layer that lists a folder and base is first, so a
        // base layer a person had hand-edited to list "Ahri" would claim it ahead of the layer the dialog just assigned it to.
        var baseKept = p.Layers.Where(l => string.Equals(l.Name, ProjectLayer.BaseLayer, StringComparison.OrdinalIgnoreCase))
            .Select(l => new ProjectLayer
            {
                Name = l.Name, Priority = l.Priority, Description = l.Description, DisplayName = l.DisplayName,
                StringOverrides = l.StringOverrides, DeclarationsKey = l.DeclarationsKey, Folders = new(),
            }).ToList();
        p.Layers = baseKept.Concat(Layers.Where(l => !l.IsBase).Select(l => new ProjectLayer
        {
            Name = l.Name.Trim(),
            Priority = l.Priority,
            Description = l.Description.Trim(),
            Folders = FolderLayers.Where(f => ReferenceEquals(f.Layer, l))
                                  .Select(f => f.Folder).ToList(),
            // M816: what an import gave the layer rides a rename and a re-prioritising; the dialog neither shows nor edits it
            DisplayName = l.Source?.DisplayName,
            StringOverrides = l.Source?.StringOverrides,
            DeclarationsKey = l.Source?.DeclarationsKey,
        })).ToList();
    }

    [RelayCommand]
    private async Task BrowseThumbnail()
    {
        var img = new FilePickerFileType("Image") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg" } };
        var f = await _dialogs.OpenFileAsync("Select a thumbnail image", img, DialogService.All);
        if (f is not null) ThumbnailPath = f;
    }

    [RelayCommand]
    private async Task BrowseGame()
    {
        var f = await _dialogs.OpenFolderAsync("Select the League of Legends 'Game' folder");
        if (f is not null)
        {
            var status = GameReferenceLibrary.Inspect(f);
            GameDirectory = status.IsValid ? status.GameDirectory! : f;
        }
    }

    [RelayCommand]
    private async Task BrowseOutput()
    {
        var f = await _dialogs.OpenFolderAsync("Select the build output folder");
        if (f is not null) OutputDirectory = f;
    }

    [RelayCommand]
    private void Save()
    {
        if (!string.IsNullOrWhiteSpace(GameDirectory) && !ValidateGameDirectory().IsValid) return;
        LayerError = ValidateLayers() ?? "";
        if (HasLayerError) return;
        Saved = true;
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
