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
        };
        // M747: the form is the window's choice, read when Create runs so a late toggle counts.
        vm.Create = (result, place) => CreateCharacterFromFolderAsync(result, place, vm.PlaceAsAnimatedProp,
            vm.PlaceAsAnimatedProp ? (float)vm.AppearAfterSeconds : 0f);
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
    private async Task<string> CreateCharacterFromFolderAsync(CharacterImportResult result, bool place, bool asAnimatedProp = true,
        float appearAfterSeconds = 0f)
    {
        if (_currentMapEntry is not { } mapEntry)
            throw new InvalidOperationException("Open the map the character belongs to first - its files are staged into that map's package.");
        if (!await EnsureProjectSavedAsync())
            throw new InvalidOperationException("Save the project before creating a character.");
        // M819: the placement ends in the map's bins; if the mod's GameData refuses them, nothing is staged for it
        if (place && await PlacementWriteRefusalAsync(mapEntry) is { } refusal) throw new InvalidOperationException(refusal);

        var package = result.Package;
        foreach (var upgrade in result.Upgrades)
            _log.Info("Character", $"{upgrade.FileName}: {upgrade.Before} → {upgrade.After}");
        foreach (var warning in result.Warnings) _log.Warn("Character", warning);

        var sources = result.Files.Select(f => (f.Path, f.Bytes)).ToList();
        var staged = WriteStagedAssets(sources, mapEntry, new List<string>(), overwrite: true);
        if (staged.Refusal is { } stagedRefusal) throw new InvalidOperationException(stagedRefusal);   // M819: the real reason
        if (staged.Missing.Count > 0)
            throw new InvalidOperationException("These files could not be staged: " + string.Join(", ", staged.Missing.Take(4)));

        string placed = place
            ? await PlaceCharacterAsync(package.Name, package.CharacterRecord, package.Skin, package.IdleClip, mapEntry, asAnimatedProp,
                appearAfterSeconds)
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
        string placed = await PlaceCharacterAsync(request.Character, request.CharacterRecord, request.Skin, request.IdleClip, mapEntry,
            request.AsAnimatedProp, request.AppearAfterSeconds);
        return placed.TrimStart();
    }

    /// <summary>
    /// The one placement path. Writes the scenery placement every shipped map uses, saves the bin,
    /// reloads the map so the prop is there, and selects it.
    /// </summary>
    private async Task<string> PlaceCharacterAsync(string character, string recordPath, string skinPath, string? idleClip,
        Core.Assets.WadAssetEntry mapEntry, bool asAnimatedProp = true, float appearAfterSeconds = 0f)
    {
        if (_currentMap is not { } map) throw new InvalidOperationException("No map is open.");
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
            throw new InvalidOperationException("The open map has no companion materials .bin, so it cannot hold placements.");
        if (await PlacementEditRefusalAsync(mapEntry) is { } refusal) throw new InvalidOperationException(refusal);   // M819: both bins the placement ends in, before the first is written (M823: a bin whose edit is kept as a declaration passes; a flow that stages files first asked the strict question already)

        byte[] target = GetAssetBytes(binEntry);
        int existing = MapContent.AllProps.Count(p => p.Prop.CharacterName.Equals(character, StringComparison.OrdinalIgnoreCase));
        string placementName = $"{character}_{existing + 1}";

        var tree = SafeBinTree.Parse(target);
        // M746: into the container the map keeps its characters in - the first one is not a safe default.
        // M747: an animated prop goes where the map keeps those, falling back to the same place.
        var id = asAnimatedProp
            ? MapPlaceableWriter.NewAnimatedPropId(tree, HashAlgorithms.Fnv1a(placementName))
            : MapPlaceableWriter.NewCharacterId(tree, HashAlgorithms.Fnv1a(placementName));
        if (!id.IsValid)
            throw new InvalidOperationException("This map has no MapPlaceableContainer, so it cannot safely hold placements.");

        var transform = System.Numerics.Matrix4x4.Identity;
        transform.Translation = GizmoPivot ?? map.Center;
        // M747: a MapAnimatedProp names its skin by number; a skin path that is not "Skins/SkinN" cannot be
        // said in that form, and is refused rather than silently placed as Skin0.
        uint skinId = 0;
        if (asAnimatedProp && !MapPlaceableWriter.TrySkinNumber(skinPath, out skinId))
            throw new InvalidOperationException($"'{skinPath}' is not a Skins/SkinN path, so it cannot be placed as an "
                + "animated prop. Untick \"Client-side prop\" to place it as a character instead.");
        var edit = asAnimatedProp
            ? new MapPlacementEdit(id)
            {
                CreateAnimatedProp = true,
                Name = placementName,
                Transform = transform,
                PropName = character,
                SkinId = skinId,
                IdleAnimation = idleClip,
                // M748: hidden until the game clock passes this; null writes no gate. Confirmed in game (M749).
                AppearAfterSeconds = appearAfterSeconds > 0 ? appearAfterSeconds : null,
            }
            : new MapPlacementEdit(id)
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

        // M722: the game preloads only the characters the map's own bin lists; one placed and not listed is
        // drawn unskinned (a WORLD_MATRIX error and a shader hash miss) and never appears. Every character on
        // the map is checked, so a prop placed before this existed is registered too.
        var onMap = MapContent.AllProps.Select(p => p.Prop.CharacterName).Append(character).ToList();
        string listed = await SavePlacementAsync(binEntry, written, mapEntry, onMap);   // M823: the two bins, as one unit when the mod's GameData changes either

        // M701: show what was just added. The prop-mesh overlay is off by default, so a placement made
        // with it off is a marker and nothing else - which reads exactly like the placement having failed.
        ShowPropMeshes = true;
        FinishWorkshopMutation();
        await LoadMapGeoAsync(mapEntry);
        if (MapContent.AllProps.FirstOrDefault(p => p.Prop.Id == id) is { } added) SelectedPropNode = added;
        _log.Success("Props", $"Placed '{placementName}' ({skinPath}) as {(asAnimatedProp ? "an animated prop" : "a character")}{(asAnimatedProp && appearAfterSeconds > 0 ? $" appearing after {appearAfterSeconds:0.#} s" : "")} at "
            + $"({transform.Translation.X:0}, {transform.Translation.Y:0}, {transform.Translation.Z:0}).");
        return $" Placed '{placementName}' at ({transform.Translation.X:0}, {transform.Translation.Y:0}, {transform.Translation.Z:0})." + listed;
    }

    /// <summary>
    /// M751: turn the selected scenery-character placement into a client-side MapAnimatedProp in place, so a
    /// prop placed before M747 spawns without being placed again. Saved at once, like a placement is, and
    /// the map reloaded - which is why unsaved placement edits are refused first: the reload would drop them.
    /// </summary>
    [RelayCommand]
    private async Task ConvertPropToClientSide()
    {
        if (SelectedPropNode is not { } node || _currentMapEntry is not { } mapEntry) return;
        if (node.Prop.IsAnimatedPropClass) { _log.Info("Props", $"'{node.Name}' is already a client-side prop."); return; }
        if (!node.Prop.Id.IsValid)
        { _log.Warn("Props", $"'{node.Name}' has no identity in the bin, so it cannot be converted."); return; }
        if (MapContent.HasPlacementEdits)
        {
            _log.Warn("Props", "Save or discard your map content edits first - converting saves the map and reloads it, "
                + "which would drop them.");
            return;
        }
        if (!TryResolveMaterialsBin(mapEntry.Path, out var binEntry))
        { _log.Error("Props", "The open map has no materials .bin."); return; }
        if (!await GuardBinEditAsync(binEntry)) return;   // M823
        if (!await EnsureProjectSavedAsync()) return;

        var id = node.Prop.Id;
        byte[] source = GetAssetBytes(binEntry);
        if (MapPlaceableWriter.WhyNotConvertible(source, id) is { } why)
        { _log.Warn("Props", $"'{node.Name}' cannot become a client-side prop: {why}"); return; }
        // Riot's own placements are spawned by the server from Riot's data whatever the mod writes; a
        // converted copy would be drawn by the client ON TOP of the server's.
        if (ReadRiotOriginalBytes(binEntry) is { } riot && MapPlaceableWriter.ShippedBy(riot, id))
        {
            _log.Warn("Props", $"'{node.Name}' is Riot's own placement: the server already spawns it from Riot's data, "
                + "so converting it would draw a second copy on top. Only placements made in ReyEngine are converted.");
            return;
        }

        byte[]? written = MapPlaceableWriter.WriteEdits(source, new[] { new MapPlacementEdit(id) { ConvertToAnimatedProp = true } },
            out string? error);
        if (written is null) { _log.Error("Props", $"'{node.Name}' could not be converted: {error}"); return; }
        if (!await SaveMapBinBytesAsync(binEntry, written))
        { _log.Error("Props", "The edited materials bin could not be saved."); return; }

        string name = node.Name;
        FinishWorkshopMutation();
        await LoadMapGeoAsync(mapEntry);
        if (MapContent.AllProps.FirstOrDefault(p => p.Prop.Id == id) is { } converted) SelectedPropNode = converted;
        _log.Success("Props", $"'{name}' is now a client-side prop (MapAnimatedProp): same place, same key, "
            + "and the game client creates it, so it spawns where a character placement never did.");
    }

    /// <summary>What registering the characters in the map's own bin comes to: the bin to save, its new bytes and what they add - or, when there is nothing to save, the sentence to say.</summary>
    private sealed record CharacterRegistration(Core.Assets.WadAssetEntry? Entry, byte[]? Bytes, IReadOnlyList<string> Added, uint List, string Said, string? FileName);

    /// <summary>M722: works out what listing every given character in the map's own bin (<c>mapNNN.bin</c>) comes to, and says why when it comes to nothing. Writes nothing.</summary>
    private CharacterRegistration PrepareCharacterRegistration(Core.Assets.WadAssetEntry mapEntry, IReadOnlyList<string> characters)
    {
        string? path = MapBinPathFor(mapEntry.Path);
        if (path is null || !TryResolveEntry(HashAlgorithms.WadPath(path), out var mapBinEntry))
        {
            _log.Warn("Props", $"No map bin at '{path ?? "?"}' - the character could not be added to the map's "
                + "character lists, so the game will not preload it.");
            return new CharacterRegistration(null, null, Array.Empty<string>(), 0, " The map bin was not found, so it is NOT in the map's character lists.", null);
        }

        byte[] bytes = GetAssetBytes(mapBinEntry);
        byte[]? written = MapCharacterListWriter.Register(bytes, characters, characters, out var added, out uint list, out var error);
        if (written is null)
        {
            _log.Warn("Props", $"{Path.GetFileName(path)}: {error} The game will not preload the placed character.");
            return new CharacterRegistration(null, null, Array.Empty<string>(), 0, " It could NOT be added to the map's character lists.", null);
        }
        if (added.Count == 0) return new CharacterRegistration(null, null, added, list, "", null);
        return new CharacterRegistration(mapBinEntry, written, added, list, "", Path.GetFileName(path));
    }

    /// <summary>The words for a registration that was saved.</summary>
    private string SaidRegistrationSaved(CharacterRegistration registration)
    {
        _log.Success("Props", $"Added {string.Join(", ", registration.Added.Select(c => "Characters/" + c))} to MapCharacterList "
            + $"0x{registration.List:x8} in {registration.FileName}, so the game preloads it.");
        return $" Added to the map's character list 0x{registration.List:x8}.";
    }

    /// <summary>M722: list every given character in the map's own bin (<c>mapNNN.bin</c>) so the game preloads
    /// it. The placement is already saved when this runs, so a failure here is reported, not thrown.</summary>
    private async Task<string> RegisterMapCharactersAsync(Core.Assets.WadAssetEntry mapEntry, IReadOnlyList<string> characters)
    {
        var registration = PrepareCharacterRegistration(mapEntry, characters);
        if (registration.Entry is null || registration.Bytes is null) return registration.Said;
        if (!await SaveMapBinBytesAsync(registration.Entry, registration.Bytes))
        {
            _log.Warn("Props", $"{registration.FileName} could not be saved - {string.Join(", ", registration.Added)} NOT added to the character lists.");
            return " The map bin could NOT be saved with the character list entry.";
        }
        return SaidRegistrationSaved(registration);
    }

    /// <summary>
    /// The two bins a placement ends in: the map's materials.bin, which holds the placement, and the map's own bin, which lists the characters the game preloads. Without the mod's GameData in either, as it always was: the
    /// placement is saved, and the registration after it (a failure of that one is reported, not thrown).
    ///
    /// <para>M823: when the GameData changes either, the two are ONE unit (<see cref="SaveMapBinsTogetherAsync"/>): both edits are declared and proven before either is kept, and one that fails after the other was kept puts it
    /// back. A placement kept without its registration is drawn unskinned and never spawns, and trying again places it twice.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The placement could not be saved; with the GameData in play, nothing of it was.</exception>
    private async Task<string> SavePlacementAsync(Core.Assets.WadAssetEntry binEntry, byte[] written, Core.Assets.WadAssetEntry mapEntry, IReadOnlyList<string> onMap)
    {
        Core.Assets.WadAssetEntry? mapBin = MapBinPathFor(mapEntry.Path) is { } mapBinPath && TryResolveEntry(HashAlgorithms.WadPath(mapBinPath), out var resolved) ? resolved : null;
        bool declared = _mounts is not null && (IsGameDataTarget(binEntry.PathHash) || (mapBin is not null && IsGameDataTarget(mapBin.PathHash)));
        if (!declared)
        {
            if (!await SaveMapBinBytesAsync(binEntry, written))
                throw new InvalidOperationException("The edited materials bin could not be saved.");
            // M722: the game preloads only the characters the map's own bin lists
            string listed = await RegisterMapCharactersAsync(mapEntry, onMap);
            return listed;
        }

        var registration = PrepareCharacterRegistration(mapEntry, onMap);
        var bins = new List<(Core.Assets.WadAssetEntry Entry, byte[] Bytes)> { (binEntry, written) };
        if (registration.Entry is { } registeredBin && registration.Bytes is { } registeredBytes) bins.Add((registeredBin, registeredBytes));
        if (await SaveMapBinsTogetherAsync(bins) is { } why) throw new InvalidOperationException(why);
        return registration.Entry is null ? registration.Said : SaidRegistrationSaved(registration);
    }
}
