using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Interop;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M580: the editor side of the Blender link.
///
/// <para>Geometry goes out, placements come back. Blender is a far better place to shape a mesh than any
/// viewport this editor will ever have, and the map's meshes are ordinary triangles — the only reason
/// they have not been editable there is that nothing carried them across.</para>
///
/// <para>M841: the link speaks protocol 3 with MapgeoAddon, which replaced the single-file "ReyEngine Bridge"
/// add-on, and it now carries MATERIALS - described from the editor's live document, their textures served
/// from the editor's own mounts, and edits coming back as edits (an assignment, a parameter, a sampler, a
/// switch, a macro). ReyEngine stays the only writer of the .mapgeo and the .materials.bin: every edit from
/// Blender lands as a pending, undoable edit that the normal Save writes.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    private BlenderBridgeServer? _blenderBridge;

    [NotifyPropertyChangedFor(nameof(BlenderLinkMenuHeader))]
    [ObservableProperty] private bool _blenderLinkActive;

    /// <summary>The menu says what the click will DO, not what the state is.</summary>
    public string BlenderLinkMenuHeader =>
        BlenderLinkActive ? "Stop Blender link (MapgeoAddon)" : "Blender link (MapgeoAddon)…";
    [ObservableProperty] private string _blenderLinkStatus = "Not running.";

    /// <summary>Port the link listens on. Loopback only; see <see cref="BlenderBridgeServer"/>.</summary>
    [ObservableProperty] private int _blenderLinkPort = BlenderBridgeProtocol.DefaultPort;

    [RelayCommand]
    private void ToggleBlenderLink()
    {
        if (_blenderBridge is { IsRunning: true })
        {
            _blenderBridge.Stop();
            BlenderLinkActive = false;
            BlenderLinkToken = "";
            BlenderLinkStatus = "Not running.";
            return;
        }

        if (_currentMap is null)
        {
            _log.Warn("Blender", "Open a map before starting the link — there would be nothing to send.");
            BlenderLinkStatus = "Open a map first.";
            return;
        }

        _blenderBridge ??= new BlenderBridgeServer(
            action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
            (level, message) =>
            {
                if (level == "warn") _log.Warn("Blender", message);
                else if (level == "success") _log.Success("Blender", message);
                else _log.Info("Blender", message);
            });

        _blenderBridge.Handlers = BlenderHandlers();
        // M841: a new token per start. A web page can reach a loopback port but cannot read this; MapgeoAddon's
        // ReyEngine panel asks for it once per session.
        BlenderLinkToken = BlenderBridgeServer.NewToken();
        _blenderBridge.Token = BlenderLinkToken;
        _blenderBridge.MapChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => Project.IsDirty = true);

        if (!_blenderBridge.Start(BlenderLinkPort, out var error))
        {
            _log.Error("Blender", error ?? "The link could not start.");
            BlenderLinkStatus = error ?? "Could not start.";
            return;
        }
        BlenderLinkActive = true;
        BlenderLinkStatus = $"Listening on 127.0.0.1:{_blenderBridge.Port} - token {BlenderLinkToken} (copied)";
        _ = CopyBlenderLinkToken();
        _log.Info("Blender", $"Link token {BlenderLinkToken} - it is on the clipboard; paste it into the Token field of MapgeoAddon's ReyEngine panel.");
    }

    /// <summary>M841: the token the running link asks for in every hello; empty while the link is stopped.</summary>
    [ObservableProperty] private string _blenderLinkToken = "";

    /// <summary>Put the token on the clipboard (done once when the link starts; the Tools menu repeats it).</summary>
    [RelayCommand]
    private async Task CopyBlenderLinkToken()
    {
        if (BlenderLinkToken.Length == 0) return;
        try { await Dialogs.CopyAsync(BlenderLinkToken); } catch { /* no clipboard (headless): the status line shows it */ }
    }

    /// <summary>M841: what the link answers with - every operation of protocol 3, all on the editor's thread.</summary>
    private BlenderBridgeHandlers BlenderHandlers() => new(
        MapName: () => _currentMapEntry?.DisplayName ?? "(no map)",
        Pull: SnapshotForBlender,
        PushTransforms: ApplyBlenderTransforms,
        PushGeometry: ApplyBlenderGeometry)
    {
        HelloInfo = HelloForBlender,
        Materials = MaterialsForBlender,
        Texture = TextureForBlender,
        AssignMaterial = AssignMaterialsFromBlender,
        EditMaterial = EditMaterialsFromBlender,
        PushGeometryEdits = ApplyBlenderGeometryEdits,
        NavGrid = NavGridForBlender,
    };

    /// <summary>
    /// Reshaped meshes from Blender, held until the map is saved.
    ///
    /// <para>Not applied immediately, unlike a placement. A reshape rewrites the mesh's vertex and index
    /// BUFFERS, which is a change to the file's bytes rather than to the decoded scene - the same reason
    /// extrude and inset are deferred. The viewport keeps showing the old shape until a save and reload,
    /// which is stated rather than left to be discovered.</para>
    /// </summary>
    private readonly Dictionary<int, MeshReshape> _blenderReshapes = new();

    public bool HasBlenderReshapes => _blenderReshapes.Count > 0;

    /// <summary>Reshapes waiting to be written, applied to the mapgeo bytes at save time.</summary>
    internal IReadOnlyList<MeshReshape> PendingBlenderReshapes => _blenderReshapes.Values.ToList();

    internal void ClearBlenderReshapes() => _blenderReshapes.Clear();

    private string ApplyBlenderGeometry(IReadOnlyList<BridgeMesh> meshes) => ApplyBlenderGeometryEdits(meshes).Detail;

    /// <summary>M841: reshapes with a per-mesh answer. A mesh the reshaper would refuse at Save is refused NOW,
    /// by name, instead of failing the whole save later. M842: shared buffers are un-shared at Save instead.</summary>
    private BridgeEditReply ApplyBlenderGeometryEdits(IReadOnlyList<BridgeMesh> meshes)
    {
        if (_currentMap is not { } map)
            return new BridgeEditReply(0, meshes.Select((m, i) => new BridgeRefusal(i, m.Name, "no map is open")).ToList(), "No map is open.");

        if (BlenderReadOnlyReason(_currentMapEntry, isBin: false) is { } readOnly)
            return new BridgeEditReply(0, meshes.Select((m, i) => new BridgeRefusal(i, m.Name, readOnly)).ToList(), readOnly);

        // M842: shared buffers are no longer refused (the reshape gets its own); several materials are, unless
        // the push says which triangle is which (a submesh table), and every material it names must exist.
        IReadOnlyDictionary<int, string> unreshapable = new Dictionary<int, string>();
        try { if (_currentMapBytes is { } bytes) unreshapable = MapGeoMeshReshaper.Refusals(bytes, ExtendedChannelMaterialsFor(_currentMapEntry?.Path)); } catch { }
        var known = new HashSet<string>(MapMaterialNames, StringComparer.Ordinal);
        foreach (var vm in MaterialEditor.Materials) known.Add(vm.Model.Name);

        int accepted = 0;
        var problems = new List<string>();
        var refused = new List<BridgeRefusal>();
        for (int i = 0; i < meshes.Count; i++)
        {
            var sent = meshes[i];
            if (sent.Submeshes.Count == 0 && unreshapable.TryGetValue(sent.Index, out var why))
            {
                string reason = $"'{sent.Name}' cannot be reshaped: {why}, and the push did not say which triangle is which";
                problems.Add(reason); refused.Add(new BridgeRefusal(i, sent.Name, reason)); continue;
            }
            if (sent.Submeshes.FirstOrDefault(x => !known.Contains(x.Material)) is { } unknown)
            {
                string reason = $"'{sent.Name}': '{unknown.Material}' is not a material of this map's materials.bin";
                problems.Add(reason); refused.Add(new BridgeRefusal(i, sent.Name, reason)); continue;
            }
            var reshape = MapGeoBlenderBridge.ToReshape(map, sent, out var problem);
            if (reshape is null)
            {
                problems.Add(problem ?? "unusable geometry"); refused.Add(new BridgeRefusal(i, sent.Name, problem ?? "unusable geometry")); continue;
            }
            _blenderReshapes[reshape.MeshIndex] = reshape;
            accepted++;
        }

        foreach (string problem in problems.Take(6)) _log.Warn("Blender", problem);
        if (problems.Count > 6) _log.Warn("Blender", $"{problems.Count - 6:n0} further problem(s) omitted.");
        if (accepted > 0)
        {
            OnPropertyChanged(nameof(HasBlenderReshapes));
            OnPropertyChanged(nameof(HasPendingMapGeoWork));   // the Save button reads this
            _log.Success("Blender", $"{accepted:n0} reshaped mesh(es) received.");

            // M584: write and reload without being asked. A reshape only exists in the FILE - it changes
            // the vertex and index buffers, not the decoded scene - so leaving it queued meant the user
            // saw nothing and had to know to press Save. Posted rather than awaited here because this
            // runs inside the request the add-on is still blocked on.
            if (BlenderAutoApply)
                Avalonia.Threading.Dispatcher.UIThread.Post(async () => await AutoApplyBlenderReshapes());
        }
        return new BridgeEditReply(accepted, refused, $"{accepted} queued, {problems.Count} refused");
    }

    /// <summary>
    /// Write pending reshapes and reload, so a push from Blender shows up on its own.
    ///
    /// <para>Off is worth having: each run rewrites the mapgeo and re-decodes it, which on a big map is a
    /// second or two, and someone nudging a shape repeatedly would rather do that once at the end.</para>
    /// </summary>
    [ObservableProperty] private bool _blenderAutoApply = true;

    private async Task AutoApplyBlenderReshapes()
    {
        if (_blenderReshapes.Count == 0) return;
        await SaveMeshMovesCommand.ExecuteAsync(null);

        // The save clears the queue only when the bytes were actually written. Still pending means it
        // refused - it has already said why - and reloading now would throw the edit away.
        if (_blenderReshapes.Count > 0)
        {
            _log.Warn("Blender", "The reshape was not written, so the map was left as it is.");
            return;
        }
        if (_currentMapEntry is { } entry && TryResolveEntry(entry.PathHash, out var reloaded))
            await LoadMapGeoAsync(reloaded);
    }

    /// <summary>Everything the open map has to offer Blender.</summary>
    private BridgeSnapshot SnapshotForBlender()
    {
        if (_currentMap is not { } map) return new BridgeSnapshot(Array.Empty<BridgeMesh>(), new[] { "No map is open." });
        var snapshot = MapGeoBlenderBridge.Snapshot(map);

        // M841: the map's own layer names, so Blender's layer collections say "Inferno" where the map does.
        var layers = (_mapVisibility.Primary?.Layers ?? Array.Empty<VisibilityLayer>())
            .Select(l => new Dictionary<string, object?> { ["name"] = l.Name, ["mask"] = l.Bit })
            .ToList();
        return snapshot with
        {
            MapInfo = new Dictionary<string, object?>
            {
                ["map"] = _currentMapEntry?.DisplayName ?? "",
                ["map_path"] = _currentMapEntry?.Path ?? "",
                ["layers"] = layers,
                ["lightmap_color_scale"] = CurrentLightmapScale,
            },
        };
    }

    /// <summary>
    /// Put Blender's placements onto the map.
    /// </summary>
    /// <remarks>
    /// MeshVerticesRevision is what tells BOTH viewports to re-upload and invalidates the ray-pick index -
    /// the transform mutates MapGeoAsset.Positions behind the same object, so nothing else notices. Missing
    /// it is the M568/M571 bug: the edit is real, saves correctly, and is invisible until a reload.
    /// </remarks>
    private string ApplyBlenderTransforms(IReadOnlyList<BridgeTransform> transforms)
    {
        if (_currentMap is not { } map) return "No map is open.";
        // M842 review: the same read-only gate as a reshape (a game map without a project, a linked bin...)
        if (BlenderReadOnlyReason(_currentMapEntry, isBin: false) is { } readOnly)
        {
            _log.Warn("Blender", "Transforms from Blender were not applied: " + readOnly);
            return readOnly;
        }

        var changed = MapGeoBlenderBridge.ApplyTransforms(map, transforms, out var problems);
        foreach (string problem in problems.Take(6)) _log.Warn("Blender", problem);
        if (problems.Count > 6) _log.Warn("Blender", $"{problems.Count - 6:n0} further problem(s) omitted.");

        if (changed.Count > 0)
        {
            if (_selection.Primary is { } primary) RefreshMeshTransformFields(primary);
            RefreshSelectionVisuals();
            MeshVerticesRevision++;
            HasMapMoves = true;
            _log.Success("Blender", $"Moved {changed.Count:n0} mesh(es) from Blender. "
                                  + "Save Map Content Edits to write it to the file.");
        }
        return $"{changed.Count} applied, {problems.Count} refused";
    }

    // ---- M841: protocol 3 - hello, materials, textures, assignments, material edits ----------------

    /// <summary>Why an asset cannot be edited from Blender, or null when it can. Asked without logging, so a
    /// question from Blender does not fill the console with refusals nobody asked ReyEngine to make.</summary>
    private string? BlenderReadOnlyReason(WadAssetEntry? entry, bool isBin)
    {
        if (entry is null) return "it is not open in ReyEngine";
        if (isBin && IsGameDataTarget(entry.PathHash))
            return GameDataEditBlocker(entry, out _, out _) is { } why ? why : null;
        if (GameDataWriteRefusal(entry.PathHash, entry.DisplayName, isBin) is { } refusal) return refusal;
        if (ProjectMode && entry.SourceKind == AssetSourceKind.RiotReference)
            return $"'{entry.DisplayName}' is a read-only Riot asset - use Copy Asset To Project in ReyEngine first";
        return null;
    }

    private WadAssetEntry? MapMaterialsBinEntry() =>
        _currentMapEntry is { } map && TryResolveMaterialsBin(map.Path, out var bin) ? bin : null;

    private Dictionary<string, object?> HelloForBlender()
    {
        var bin = MapMaterialsBinEntry();
        return new Dictionary<string, object?>
        {
            ["project"] = Project.Name,
            ["project_root"] = Project.RootPath,
            ["map_path"] = _currentMapEntry?.Path ?? "",
            ["materials_bin"] = bin?.Path ?? "",
            ["mesh_count"] = _currentMap?.Meshes.Count ?? 0,
            ["map_read_only_reason"] = _currentMap is null ? "no map is open" : BlenderReadOnlyReason(_currentMapEntry, isBin: false),
            ["materials_read_only_reason"] = bin is null ? "the map has no materials.bin" : BlenderReadOnlyReason(bin, isBin: true),
            ["navgrid"] = _navGridEntry?.Path,
        };
    }

    /// <summary>Make the Materials tab hold the open map's materials.bin, the way opening the map does - but
    /// synchronously, because a request from Blender is waiting for the answer. A tab holding ANOTHER bin
    /// with unsaved edits is left alone: replacing it would throw those edits away.</summary>
    private bool EnsureMapMaterialsInEditor(WadAssetEntry bin, out string? reason)
    {
        reason = null;
        // The same bin means the same chunk FROM THE SAME SOURCE: after Copy To Project the path resolves to the
        // project's copy while the tab may still hold the Riot reference it read before - edits made there would
        // be edits of a document nothing saves.
        var held = MaterialEditor.BinEntry;
        bool same = held is not null && held.PathHash == bin.PathHash && held.SourceKind == bin.SourceKind
                    && MaterialEditor.StaleReason is null;
        if (same) return true;
        if (held is not null && MaterialEditor.IsDirty)
        {
            reason = held.PathHash == bin.PathHash
                ? $"ReyEngine's Materials tab holds unsaved edits to an older copy of {held.DisplayName} (it now resolves to the {bin.SourceKind} copy); save or revert them first"
                : $"ReyEngine's Materials tab holds unsaved edits to {held.DisplayName}; save or revert them first";
            return false;
        }
        if (held is not null && MaterialEditor.UncommittedRowEdit() is { } typed)
        {
            reason = $"ReyEngine's Materials tab has a value typed and not applied ({typed}); apply or revert it first";
            return false;
        }
        try
        {
            byte[] bytes = GetAssetBytes(bin);
            var doc = MaterialDocument.Parse(bytes, ResolveBinName, ResolveWadPath, ReadAssetByPath);
            MaterialEditor.Load(doc, bin, bytes);
            HasMaterialData = true;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"{bin.DisplayName} could not be read: {ex.Message}";
            return false;
        }
    }

    private Dictionary<string, object?> MaterialsForBlender()
    {
        if (_currentMap is not { } map || MapMaterialsBinEntry() is not { } bin)
            return new Dictionary<string, object?> { ["materials"] = new Dictionary<string, object?>(), ["count"] = 0,
                ["read_only_reason"] = _currentMap is null ? "no map is open" : "the map has no materials.bin" };

        string? readOnly = BlenderReadOnlyReason(bin, isBin: true);
        IEnumerable<MaterialBinding> bindings;
        bool live = EnsureMapMaterialsInEditor(bin, out string? whyNotLive);
        if (live) bindings = MaterialEditor.Materials.Select(vm => vm.Model);
        else
        {
            // The Materials tab is busy with another bin: describe the file instead, and say edits are refused.
            readOnly ??= whyNotLive;
            bindings = MaterialDocument.Parse(GetAssetBytes(bin), ResolveBinName, ResolveWadPath, ReadAssetByPath).Materials;
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mesh in map.Meshes)
            for (int k = 0; k < Math.Max(1, mesh.Materials.Count); k++)
                if (mesh.EffectiveSubmeshMaterial(k) is { Length: > 0 } name) used.Add(name);

        var materials = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var binding in bindings)
            if (!materials.ContainsKey(binding.Name))   // M789: a repeated name keeps the FIRST, like every reader here
                materials[binding.Name] = BlenderLinkMaterials.Describe(binding, used.Contains(binding.Name), readOnly);

        return new Dictionary<string, object?>
        {
            ["bin"] = bin.Path,
            ["live"] = live,
            ["count"] = materials.Count,
            ["used"] = used.Count(materials.ContainsKey),
            ["editable"] = readOnly is null,
            ["read_only_reason"] = readOnly,
            ["lightmap_color_scale"] = CurrentLightmapScale,
            ["materials"] = materials,
        };
    }

    /// <summary>A texture's bytes from the editor's mounts - the project first, then the game - so Blender
    /// needs no WAD cache of its own. A .tex asked for and only a .dds present (or the other way) answers
    /// with the sibling, which is what MapgeoAddon's resolver tries as well.</summary>
    private BridgeTexture TextureForBlender(string request)
    {
        if (!ContentLoaded) return new BridgeTexture(false, request, 0, "", "", null, "no project is open");

        var candidates = new List<(ulong Hash, string Path)>();
        if (BlenderLinkRequests.TryParseLink(request, out ulong link))
            candidates.Add((link, ResolveWadPath(link) ?? ""));
        else
        {
            string path = request.Trim().Replace('\\', '/');
            candidates.Add((HashAlgorithms.WadPath(path), path));
            string ext = Path.GetExtension(path);
            if (ext.Equals(".tex", StringComparison.OrdinalIgnoreCase))
                candidates.Add((HashAlgorithms.WadPath(Path.ChangeExtension(path, ".dds")), Path.ChangeExtension(path, ".dds")));
            else if (ext.Equals(".dds", StringComparison.OrdinalIgnoreCase))
                candidates.Add((HashAlgorithms.WadPath(Path.ChangeExtension(path, ".tex")), Path.ChangeExtension(path, ".tex")));
        }

        static bool IsTexture(string p) =>
            p.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
        if (candidates[0].Path.Length > 0 && !IsTexture(candidates[0].Path))
            return new BridgeTexture(false, candidates[0].Path, candidates[0].Hash, "", "", null, "only .tex and .dds textures are served");

        foreach (var (hash, path) in candidates)
        {
            if (!TryResolveEntry(hash, out var entry)) continue;
            byte[] bytes;
            try { bytes = GetAssetBytes(entry); }
            catch (Exception ex) { return new BridgeTexture(false, path, hash, "", "", null, $"could not be read: {ex.Message}"); }
            string resolved = entry.IsResolved ? entry.Path : path;
            string extension = Path.GetExtension(resolved) is { Length: > 0 } e ? e.ToLowerInvariant() : Sniff(bytes);
            if (extension is not (".tex" or ".dds"))
                return new BridgeTexture(false, resolved, hash, "", "", null, "only .tex and .dds textures are served");
            string source = entry.SourceKind switch
            {
                AssetSourceKind.RiotReference => "game",
                AssetSourceKind.LtkGameData => "gamedata",
                _ => "project",
            };
            return new BridgeTexture(true, resolved, hash, extension, source, bytes, null);
        }
        return new BridgeTexture(false, candidates[0].Path, candidates[0].Hash, "", "", null, "not in the project or the game");

        static string Sniff(byte[] b) =>
            b.Length >= 4 && b[0] == (byte)'T' && b[1] == (byte)'E' && b[2] == (byte)'X' ? ".tex"
            : b.Length >= 4 && b[0] == (byte)'D' && b[1] == (byte)'D' && b[2] == (byte)'S' ? ".dds" : "";
    }

    /// <summary>
    /// Assignments from Blender, as pending mesh material edits - the same edit the inspector's material
    /// picker makes (written by Save Map Content Edits, one undo step per mesh). A submesh of a mesh with
    /// several gets its own edit; a mesh whose submeshes all end up on one material gets the whole-mesh edit.
    /// </summary>
    private BridgeEditReply AssignMaterialsFromBlender(IReadOnlyList<BridgeAssignment> assignments)
    {
        var refused = new List<BridgeRefusal>();
        if (_currentMap is not { } map)
            return new BridgeEditReply(0, assignments.Select((a, i) => new BridgeRefusal(i, $"mesh {a.Mesh}", "no map is open")).ToList(), "no map is open");
        if (BlenderReadOnlyReason(_currentMapEntry, isBin: false) is { } readOnly)
            return new BridgeEditReply(0, assignments.Select((a, i) => new BridgeRefusal(i, $"mesh {a.Mesh}", readOnly)).ToList(), readOnly);

        // What a submesh may be assigned: a material the map's bin defines.
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (MapMaterialsBinEntry() is { } bin && EnsureMapMaterialsInEditor(bin, out _))
            foreach (var vm in MaterialEditor.Materials) known.TryAdd(vm.Model.Name, vm.Model.Name);
        foreach (string name in MapMaterialNames) known.TryAdd(name, name);

        var byIndex = map.Meshes.ToDictionary(m => m.Index);
        int applied = 0;
        foreach (var group in assignments.Select((a, i) => (a, i)).GroupBy(x => x.a.Mesh))
        {
            if (!byIndex.TryGetValue(group.Key, out var mesh))
            {
                foreach (var (a, i) in group) refused.Add(new BridgeRefusal(i, $"mesh {a.Mesh}", "is not in the open map"));
                continue;
            }
            var before = MeshMaterialAssignmentCommand.Capture(mesh);
            int submeshes = Math.Max(1, mesh.Materials.Count);
            var target = Enumerable.Range(0, submeshes).Select(mesh.EffectiveSubmeshMaterial).ToArray();
            bool any = false;
            foreach (var (a, i) in group)
            {
                if (!known.TryGetValue(a.Material, out var material))
                { refused.Add(new BridgeRefusal(i, $"{mesh.Name}", $"'{a.Material}' is not a material of this map's materials.bin")); continue; }
                if (a.Submesh >= submeshes)
                { refused.Add(new BridgeRefusal(i, $"{mesh.Name}", $"has {submeshes} submesh(es), not {a.Submesh + 1}")); continue; }
                if (a.Submesh < 0) for (int k = 0; k < submeshes; k++) target[k] = material;
                else target[a.Submesh] = material;
                any = true;
                applied++;
            }
            if (!any) continue;

            var after = MeshMaterialAssignmentCommand.Normalize(mesh, target);
            if (after.Equals(before)) continue;
            after.ApplyTo(mesh);
            UndoService.PushApplied(new MeshMaterialAssignmentCommand(_currentMap, mesh, before, after, AfterMeshMaterialEdit));
        }

        if (applied > 0)
        {
            AfterMeshMaterialEdit();
            _log.Success("Blender", $"{applied:n0} material assignment(s) from Blender. Save Map Content Edits writes them to the mapgeo.");
        }
        return new BridgeEditReply(applied, refused, $"{applied} applied, {refused.Count} refused");
    }

    /// <summary>
    /// Material edits from Blender, through the Materials tab's own commands - each one an undo step,
    /// marked dirty, written by the normal Save. Linked-bin and read-only materials are refused with the
    /// reason, as the tab itself would refuse them.
    /// </summary>
    private BridgeEditReply EditMaterialsFromBlender(IReadOnlyList<BridgeMaterialEdit> edits)
    {
        string? whole = _currentMap is null ? "no map is open"
            : MapMaterialsBinEntry() is not { } bin ? "the map has no materials.bin"
            : BlenderReadOnlyReason(bin, isBin: true) is { } ro ? ro
            : !EnsureMapMaterialsInEditor(bin, out var why) ? why
            : null;
        if (whole is not null)
            return new BridgeEditReply(0, edits.Select((e, i) => new BridgeRefusal(i, Describe(e), whole)).ToList(), whole);

        var refused = new List<BridgeRefusal>();
        int applied = 0, unchanged = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            LinkEditOutcome outcome;
            try { outcome = MaterialEditor.ApplyLinkEdit(edits[i]); }
            catch (Exception ex) { outcome = LinkEditOutcome.Refused(ex.Message); }
            if (outcome.Refusal is { } reason) refused.Add(new BridgeRefusal(i, Describe(edits[i]), reason));
            else if (outcome.Applied) applied++;
            else unchanged++;   // already that value: not an edit, not a refusal
        }
        if (applied > 0)
        {
            HasMaterialData = true;
            _log.Success("Blender", $"{applied:n0} material edit(s) from Blender. Save the materials (Materials tab ▸ Save) to write them.");
        }
        return new BridgeEditReply(applied, refused, $"{applied} applied, {unchanged} unchanged, {refused.Count} refused");

        static string Describe(BridgeMaterialEdit e) => $"{e.Material}: {e.Kind} {e.Name}";
    }
}
