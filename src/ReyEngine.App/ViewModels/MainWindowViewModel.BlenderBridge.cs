using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Formats.Interop;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M580: the editor side of the Blender link.
///
/// <para>Geometry goes out, placements come back. Blender is a far better place to shape a mesh than any
/// viewport this editor will ever have, and the map's meshes are ordinary triangles — the only reason
/// they have not been editable there is that nothing carried them across.</para>
///
/// <para>Materials deliberately do not cross. They live in the companion .bin, bound to submesh ranges and
/// to shader state Blender cannot represent, and a round trip through an .blend would be a good way to
/// lose them. The link is about shape and placement.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    private BlenderBridgeServer? _blenderBridge;

    [NotifyPropertyChangedFor(nameof(BlenderLinkMenuHeader))]
    [ObservableProperty] private bool _blenderLinkActive;

    /// <summary>The menu says what the click will DO, not what the state is.</summary>
    public string BlenderLinkMenuHeader =>
        BlenderLinkActive ? "Stop Blender Link" : "Start Blender Link...";
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

        _blenderBridge.Handlers = new BlenderBridgeHandlers(
            MapName: () => _currentMapEntry?.DisplayName ?? "(no map)",
            Pull: SnapshotForBlender,
            PushTransforms: ApplyBlenderTransforms,
            PushGeometry: ApplyBlenderGeometry);
        _blenderBridge.MapChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => Project.IsDirty = true);

        if (!_blenderBridge.Start(BlenderLinkPort, out var error))
        {
            _log.Error("Blender", error ?? "The link could not start.");
            BlenderLinkStatus = error ?? "Could not start.";
            return;
        }
        BlenderLinkActive = true;
        BlenderLinkStatus = $"Listening on 127.0.0.1:{_blenderBridge.Port}.";
    }

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

    private string ApplyBlenderGeometry(IReadOnlyList<BridgeMesh> meshes)
    {
        if (_currentMap is not { } map) return "No map is open.";

        int accepted = 0;
        var problems = new List<string>();
        foreach (var sent in meshes)
        {
            var reshape = MapGeoBlenderBridge.ToReshape(map, sent, out var reason);
            if (reshape is null) { problems.Add(reason ?? "unusable geometry"); continue; }
            _blenderReshapes[reshape.MeshIndex] = reshape;
            accepted++;
        }

        foreach (string problem in problems.Take(6)) _log.Warn("Blender", problem);
        if (problems.Count > 6) _log.Warn("Blender", $"{problems.Count - 6:n0} further problem(s) omitted.");
        if (accepted > 0)
        {
            OnPropertyChanged(nameof(HasBlenderReshapes));
            OnPropertyChanged(nameof(HasPendingMapGeoWork));   // the Save button reads this
            _log.Success("Blender", $"{accepted:n0} reshaped mesh(es) queued. Save Map Content Edits to write "
                + "them; the viewport keeps the old shape until the map is reloaded.");
        }
        return $"{accepted} queued, {problems.Count} refused";
    }

    /// <summary>Everything the open map has to offer Blender.</summary>
    private BridgeSnapshot SnapshotForBlender()
    {
        if (_currentMap is not { } map) return new BridgeSnapshot(Array.Empty<BridgeMesh>(), new[] { "No map is open." });
        return MapGeoBlenderBridge.Snapshot(map);
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

        var changed = MapGeoBlenderBridge.ApplyTransforms(map, transforms, out var problems);
        foreach (string problem in problems.Take(6)) _log.Warn("Blender", problem);
        if (problems.Count > 6) _log.Warn("Blender", $"{problems.Count - 6:n0} further problem(s) omitted.");

        if (changed.Count > 0)
        {
            if (_selection.Primary is { } primary) RefreshMeshTransformFields(primary);
            RefreshSelectionVisuals();
            MeshVerticesRevision++;
            _log.Success("Blender", $"Moved {changed.Count:n0} mesh(es) from Blender. "
                                  + "Save Map Content Edits to write it to the file.");
        }
        return $"{changed.Count} applied, {problems.Count} refused";
    }
}
