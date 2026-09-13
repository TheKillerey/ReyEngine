using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Meta;
using SkiaSharp;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M697: the host half of the Character Creator and of Add prop to map.
///
/// <para>The creator window reads a folder and builds the package (Formats owns both); this stages the
/// result into the open map's own package. Staging goes through the same writer every Workshop import
/// uses, so a folder project gets loose files under the map's WAD folder and a packed one gets overrides -
/// with one difference: a re-created character OVERWRITES its own files, because the creator authored
/// every one of those paths and skipping them would leave the bins of the previous attempt in place while
/// reporting success.</para>
///
/// <para>Placing is one method for both windows. A character you just imported and one the map has
/// carried since Riot shipped it become the same scenery placement, through the same writer verb.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    public Action<CharacterCreatorViewModel>? ShowCharacterCreatorWindow;
    public Action<AddPropViewModel>? ShowAddPropWindow;

    [RelayCommand]
    private void OpenCharacterCreator()
    {
        var vm = new CharacterCreatorViewModel
        {
            CanPlace = _currentMap is not null && _currentMapEntry is not null,
            PickFolder = () => Dialogs.OpenFolderAsync("Pick the character folder (its .skn, .skl, animations and texture)"),
            DecodeImage = DecodeCharacterImage,
            Create = CreateCharacterFromFolderAsync,
        };
        ShowCharacterCreatorWindow?.Invoke(vm);
    }

    /// <summary>M697: every character the open map's package carries, ready to place.</summary>
    [RelayCommand]
    private void OpenAddProp()
    {
        if (_currentMap is null || _currentMapEntry is null)
        { _log.Warn("Props", "Open a map before adding a prop to it."); return; }

        var vm = new AddPropViewModel
        {
            ReadAsset = ReadAssetByPath,
            ResolveBinName = ResolveBinName,
            ResolveWadPath = ResolveWadPath,
            Add = AddPropToMapAsync,
        };
        vm.Load(AssetEntries.Where(e => e.IsResolved).Select(e => e.Path));
        ShowAddPropWindow?.Invoke(vm);
    }

    /// <summary>The formats Core cannot read on its own. A .png is the one the creator is likely to meet -
    /// somebody's export beside the mesh - and Skia is already here for the GIF backdrop.</summary>
    internal static TextureImage? DecodeCharacterImage(byte[] bytes)
    {
        using var decoded = SKBitmap.Decode(bytes);
        if (decoded is null) return null;
        using var rgba = decoded.Copy(SKColorType.Rgba8888);
        return rgba is null ? null : new TextureImage(rgba.Width, rgba.Height, rgba.Bytes);
    }

    /// <summary>Stage the finished character into the open map's package and, when
    /// <paramref name="place"/>, add a scenery placement of it at the gizmo. Returns the line the window
    /// shows; throws with the reason when it cannot.</summary>
    private async Task<string> CreateCharacterFromFolderAsync(CharacterImportResult result, bool place)
    {
        if (_currentMapEntry is not { } mapEntry)
            throw new InvalidOperationException("Open the map the character belongs to first - its files are staged into that map's package.");
        if (!await EnsureProjectSavedAsync())
            throw new InvalidOperationException("Save the project before creating a character.");

        var package = result.Package;
        foreach (var upgrade in result.Upgrades)
            _log.Info("Character", $"{upgrade.FileName}: {upgrade.Before} → {upgrade.After}");
        foreach (var warning in result.Warnings) _log.Warn("Character", warning);

        var sources = result.Files.Select(f => (f.Path, f.Bytes)).ToList();
        var staged = WriteStagedAssets(sources, mapEntry, new List<string>(), overwrite: true);
        if (staged.Missing.Count > 0)
            throw new InvalidOperationException("These files could not be staged: " + string.Join(", ", staged.Missing.Take(4)));

        string placed = place
            ? await PlaceCharacterAsync(package.Name, package.CharacterRecord, package.Skin, package.IdleClip, mapEntry)
            : "";
        if (!place) { FinishWorkshopMutation(); await LoadMapGeoAsync(mapEntry); }

        string upgraded = result.Upgrades.Count == 0 ? "every file was already current" : $"{result.Upgrades.Count} file(s) upgraded";
        _log.Success("Character", $"'{package.Name}': {staged.Written} file(s) staged, {upgraded}, "
            + $"{package.ClipPaths.Count} clip(s) in its animation graph.");
        return $"'{package.Name}' created: {staged.Written} file(s) staged, {upgraded}." + placed;
    }

    /// <summary>M697: place a character the package already carries.</summary>
    private async Task<string> AddPropToMapAsync(AddPropRequest request)
    {
        if (_currentMapEntry is not { } mapEntry)
            throw new InvalidOperationException("Open a map before adding a prop to it.");
        if (!await EnsureProjectSavedAsync())
            throw new InvalidOperationException("Save the project before adding a prop.");
        string placed = await PlaceCharacterAsync(request.Character, request.CharacterRecord, request.Skin, request.IdleClip, mapEntry);
        return placed.TrimStart();
    }

    /// <summary>
    /// The one placement path. Writes the scenery placement every shipped map uses, saves the bin,
    /// reloads the map so the prop is there, and selects it.
    /// </summary>
    private async Task<string> PlaceCharacterAsync(string character, string recordPath, string skinPath, string? idleClip,
        Core.Assets.WadAssetEntry mapEntry)
    {
        if (_currentMap is not { } map) throw new InvalidOperationException("No map is open.");
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
            throw new InvalidOperationException("The open map has no companion materials .bin, so it cannot hold placements.");

        byte[] target = GetAssetBytes(binEntry);
        int existing = MapContent.AllProps.Count(p => p.Prop.CharacterName.Equals(character, StringComparison.OrdinalIgnoreCase));
        string placementName = $"{character}_{existing + 1}";

        var tree = SafeBinTree.Parse(target);
        var id = MapPlaceableWriter.NewParticleId(tree, HashAlgorithms.Fnv1a(placementName));
        if (!id.IsValid)
            throw new InvalidOperationException("This map has no MapPlaceableContainer, so it cannot safely hold placements.");

        var transform = System.Numerics.Matrix4x4.Identity;
        transform.Translation = GizmoPivot ?? map.Center;
        var edit = new MapPlacementEdit(id)
        {
            CreateCharacter = true,
            Name = placementName,
            Transform = transform,
            CharacterRecord = recordPath,
            Skin = skinPath,
            IdleAnimation = idleClip,
        };
        byte[] written = MapPlaceableWriter.WriteEdits(target, new[] { edit }, out var error)
            ?? throw new InvalidOperationException(error ?? "The character placement could not be created.");
        if (!await SaveMapBinBytesAsync(binEntry, written))
            throw new InvalidOperationException("The edited materials bin could not be saved.");

        // M722: the game preloads only the characters the map's own bin lists; one placed and not listed is
        // drawn unskinned (a WORLD_MATRIX error and a shader hash miss) and never appears. Every character on
        // the map is checked, so a prop placed before this existed is registered too.
        var onMap = MapContent.AllProps.Select(p => p.Prop.CharacterName).Append(character).ToList();
        string listed = await RegisterMapCharactersAsync(mapEntry, onMap);

        // M701: show what was just added. The prop-mesh overlay is off by default, so a placement made
        // with it off is a marker and nothing else - which reads exactly like the placement having failed.
        ShowPropMeshes = true;
        FinishWorkshopMutation();
        await LoadMapGeoAsync(mapEntry);
        if (MapContent.AllProps.FirstOrDefault(p => p.Prop.Id == id) is { } added) SelectedPropNode = added;
        _log.Success("Props", $"Placed '{placementName}' ({skinPath}) at "
            + $"({transform.Translation.X:0}, {transform.Translation.Y:0}, {transform.Translation.Z:0}).");
        return $" Placed '{placementName}' at ({transform.Translation.X:0}, {transform.Translation.Y:0}, {transform.Translation.Z:0})." + listed;
    }

    /// <summary>M722: list every given character in the map's own bin (<c>mapNNN.bin</c>) so the game preloads
    /// it. The placement is already saved when this runs, so a failure here is reported, not thrown.</summary>
    private async Task<string> RegisterMapCharactersAsync(Core.Assets.WadAssetEntry mapEntry, IReadOnlyList<string> characters)
    {
        string? path = MapBinPathFor(mapEntry.Path);
        if (path is null || !TryResolveEntry(HashAlgorithms.WadPath(path), out var mapBinEntry))
        {
            _log.Warn("Props", $"No map bin at '{path ?? "?"}' - the character could not be added to the map's "
                + "character lists, so the game will not preload it.");
            return " The map bin was not found, so it is NOT in the map's character lists.";
        }

        byte[] bytes = GetAssetBytes(mapBinEntry);
        byte[]? written = MapCharacterListWriter.Register(bytes, characters, characters, out var added, out uint list, out var error);
        if (written is null)
        {
            _log.Warn("Props", $"{Path.GetFileName(path)}: {error} The game will not preload the placed character.");
            return " It could NOT be added to the map's character lists.";
        }
        if (added.Count == 0) return "";
        if (!await SaveMapBinBytesAsync(mapBinEntry, written))
        {
            _log.Warn("Props", $"{Path.GetFileName(path)} could not be saved - {string.Join(", ", added)} NOT added to the character lists.");
            return " The map bin could NOT be saved with the character list entry.";
        }
        _log.Success("Props", $"Added {string.Join(", ", added.Select(c => "Characters/" + c))} to MapCharacterList "
            + $"0x{list:x8} in {Path.GetFileName(path)}, so the game preloads it.");
        return $" Added to the map's character list 0x{list:x8}.";
    }
}
