using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>What the editor needs from the open map. Supplied by the main view model so this one never
/// reaches into project/asset state itself.</summary>
public sealed record UvEditorContext(
    MapGeoAsset Map, byte[] MapGeoBytes, IReadOnlySet<string>? ExtendedChannelMaterials,
    IReadOnlyList<int> SelectedMeshIndices, bool HasPendingEdits, string MapName);

/// <summary>One mesh in the picker.</summary>
public sealed record UvMeshItem(int Index, string Label, bool HasUv7, bool IsSelectedInViewport)
{
    public override string ToString() => Label;
}

/// <summary>
/// M492: view and edit the SECOND UV set (Texcoord7) of the open map's meshes.
///
/// <para>Both halves exist because of the same defect class. M479 found the porter sampling the four-blend
/// blend mask with the wrong UV set, and it was only caught by dumping numbers into a scratchpad probe -
/// nothing in the editor could show what a mesh's UVs actually looked like. A layout that leaves the unit
/// square, or a channel that is a world canvas where an atlas was expected, is obvious on sight and
/// invisible in a property grid.</para>
///
/// <para>The edit is deliberately narrow: it overwrites floats in a channel that already exists and never
/// touches the declaration, stride or buffer length (see <see cref="MeshUvChannelEditor"/>). Creating the
/// channel remains "Add Texcoord7", which is the operation that changes layout and therefore carries the
/// M476-class risk.</para>
/// </summary>
public sealed partial class UvEditorViewModel : ObservableObject
{
    private readonly Func<UvEditorContext?> _loadContext;
    private readonly Func<byte[], UvEditResult, Task> _save;
    private UvEditorContext? _context;

    /// <summary>Wireframe cap. A mapgeo mesh can carry hundreds of thousands of triangles; past a few
    /// thousand edges the picture stops changing and the UI thread starts stalling. Triangles are sampled
    /// across the whole range rather than truncated, so the shape stays representative.</summary>
    private const int MaxEdges = 6000;

    public UvEditorViewModel(Func<UvEditorContext?> loadContext, Func<byte[], UvEditResult, Task> save)
    {
        _loadContext = loadContext;
        _save = save;
        Refresh();
    }

    public ObservableCollection<UvMeshItem> Meshes { get; } = new();

    [ObservableProperty] private UvMeshItem? _selectedMesh;
    [ObservableProperty] private bool _showTexcoord0 = true;
    [ObservableProperty] private bool _onlySelection = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _channelInfo = "";
    [ObservableProperty] private string _mapName = "";
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private Vector2[]? _segments;
    [ObservableProperty] private Vector2[]? _compareSegments;

    // ---- edit parameters -------------------------------------------------
    public IReadOnlyList<string> Modes { get; } = new[]
    {
        "World planar XZ (canvas)",
        "Copy from Texcoord0",
        "Scale / offset the existing UVs",
    };
    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private double _scaleU = 1, _scaleV = 1, _offsetU, _offsetV;

    private UvEditMode Mode => ModeIndex switch
    {
        0 => UvEditMode.WorldPlanarXz,
        1 => UvEditMode.CopyTexcoord0,
        _ => UvEditMode.ScaleExisting,
    };

    /// <summary>The rect the planar projection normalises over is the map's own XZ extent; showing it makes
    /// the projection legible instead of magic.</summary>
    public string CanvasRectInfo { get; private set; } = "";

    partial void OnSelectedMeshChanged(UvMeshItem? value) => RedrawSelected();
    partial void OnShowTexcoord0Changed(bool value) => RedrawSelected();
    partial void OnOnlySelectionChanged(bool value) => Refresh();
    partial void OnModeIndexChanged(int value) => OnPropertyChanged(nameof(ModeHint));

    public string ModeHint => Mode switch
    {
        UvEditMode.WorldPlanarXz =>
            "Projects world XZ onto the map's bounds, normalised into 0..1. Measured in M479, this is what "
            + "the legacy NVR second UV set actually is (u vs world X fits at R^2 = 0.9997).",
        UvEditMode.CopyTexcoord0 =>
            "Copies the diffuse UVs. Valid and in range, but charts overlap between meshes — this is not a "
            + "lightmap unwrap.",
        _ => "Keeps the current UVs and applies scale, then offset. The tuning pass.",
    };

    [RelayCommand]
    public void Refresh()
    {
        _context = _loadContext();
        Meshes.Clear();
        Segments = null;
        CompareSegments = null;

        if (_context is not { } ctx)
        { Status = "No map is open."; MapName = ""; ChannelInfo = ""; return; }

        MapName = ctx.MapName;
        var selected = ctx.SelectedMeshIndices.ToHashSet();
        foreach (var mesh in ctx.Map.Meshes)
        {
            if (OnlySelection && selected.Count > 0 && !selected.Contains(mesh.Index)) continue;
            string material = ctx.Map.Groups.FirstOrDefault(g => g.MeshIndex == mesh.Index)?.Material ?? "";
            int slash = material.LastIndexOf('/');
            if (slash >= 0) material = material[(slash + 1)..];
            Meshes.Add(new UvMeshItem(mesh.Index,
                $"#{mesh.Index}  {mesh.VertexCount:n0} vtx  {material}" + (mesh.HasLightmapUv ? "" : "   (no UV7)"),
                mesh.HasLightmapUv, selected.Contains(mesh.Index)));
        }

        var bounds = MeshUvChannelEditor.WorldXzBounds(Editable(ctx) ?? new MapGeoBinary());
        CanvasRectInfo = $"canvas rect  X {bounds.Min.X:0}..{bounds.Max.X:0}   Z {bounds.Min.Y:0}..{bounds.Max.Y:0}";
        OnPropertyChanged(nameof(CanvasRectInfo));

        int withUv7 = Meshes.Count(m => m.HasUv7);
        Status = Meshes.Count == 0
            ? (OnlySelection && selected.Count > 0 ? "No selected mesh to show." : "This map has no meshes.")
            : $"{Meshes.Count:n0} mesh(es), {withUv7:n0} with a Texcoord7 channel.";
        SelectedMesh = Meshes.FirstOrDefault(m => m.HasUv7) ?? Meshes.FirstOrDefault();
    }

    private MapGeoBinary? Editable(UvEditorContext ctx)
    {
        try
        {
            return MapGeoBinary.TryReadEditable(ctx.MapGeoBytes, out var map, ctx.ExtendedChannelMaterials)
                ? map : null;
        }
        catch { return null; }
    }

    /// <summary>Rebuild the wireframe for the picked mesh. Reads the DECODED asset, which is the same data
    /// the viewport draws, so the picture cannot drift from what is on screen.</summary>
    private void RedrawSelected()
    {
        Segments = null;
        CompareSegments = null;
        ChannelInfo = "";
        if (_context is not { } ctx || SelectedMesh is not { } item) return;

        var mesh = ctx.Map.Meshes.FirstOrDefault(m => m.Index == item.Index);
        if (mesh is null) return;

        // RawLightmapUvs is Texcoord7 BEFORE the atlas scale/bias, which is what is actually stored in the
        // vertex buffer and therefore what an edit here writes. LightmapUvs is the transformed one and would
        // show a different picture from the bytes.
        Segments = BuildEdges(ctx, mesh, ctx.Map.RawLightmapUvs);
        if (ShowTexcoord0) CompareSegments = BuildEdges(ctx, mesh, ctx.Map.Uvs);

        ChannelInfo = Describe("UV7", ctx, mesh, ctx.Map.RawLightmapUvs)
                      + (ShowTexcoord0 ? "     " + Describe("UV0", ctx, mesh, ctx.Map.Uvs) : "");
    }

    private static string Describe(string label, UvEditorContext ctx, MapGeoMesh mesh, float[]? uvs)
    {
        if (uvs is null) return $"{label}: absent";
        int start = mesh.VertexStart, end = mesh.VertexStart + mesh.VertexCount;
        if (end * 2 > uvs.Length) return $"{label}: absent";

        float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
        int outside = 0;
        for (int i = start; i < end; i++)
        {
            float u = uvs[i * 2], v = uvs[i * 2 + 1];
            u0 = Math.Min(u0, u); u1 = Math.Max(u1, u);
            v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
            if (u < 0 || u > 1 || v < 0 || v > 1) outside++;
        }
        if (u0 > u1) return $"{label}: absent";
        string pct = mesh.VertexCount == 0 ? "0" : (100.0 * outside / mesh.VertexCount).ToString("0.#", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture,
            $"{label}: u {u0:0.###}..{u1:0.###}  v {v0:0.###}..{v1:0.###}  ({pct}% outside 0..1)");
    }

    /// <summary>Triangle edges for one mesh, in UV space, sampled down to the draw cap.</summary>
    private static Vector2[]? BuildEdges(UvEditorContext ctx, MapGeoMesh mesh, float[]? uvs)
    {
        if (uvs is null) return null;
        int vEnd = mesh.VertexStart + mesh.VertexCount;
        if (vEnd * 2 > uvs.Length) return null;

        var groups = ctx.Map.Groups.Where(g => g.MeshIndex == mesh.Index).ToList();
        if (groups.Count == 0) return null;

        int triangles = groups.Sum(g => g.IndexCount) / 3;
        if (triangles == 0) return null;
        int step = Math.Max(1, (int)Math.Ceiling(triangles / (double)(MaxEdges / 3)));

        var indices = ctx.Map.Indices;
        var edges = new List<Vector2>(Math.Min(MaxEdges, triangles * 6));
        int seen = 0;
        foreach (var g in groups)
        {
            for (int t = 0; t + 2 < g.IndexCount; t += 3, seen++)
            {
                if (seen % step != 0) continue;
                int i0 = (int)indices[g.StartIndex + t];
                int i1 = (int)indices[g.StartIndex + t + 1];
                int i2 = (int)indices[g.StartIndex + t + 2];
                if (i0 < 0 || i2 < 0 || i0 * 2 + 1 >= uvs.Length || i1 * 2 + 1 >= uvs.Length || i2 * 2 + 1 >= uvs.Length)
                    continue;

                var a = new Vector2(uvs[i0 * 2], uvs[i0 * 2 + 1]);
                var b = new Vector2(uvs[i1 * 2], uvs[i1 * 2 + 1]);
                var c = new Vector2(uvs[i2 * 2], uvs[i2 * 2 + 1]);
                edges.Add(a); edges.Add(b);
                edges.Add(b); edges.Add(c);
                edges.Add(c); edges.Add(a);
                if (edges.Count >= MaxEdges) return edges.ToArray();
            }
        }
        return edges.Count == 0 ? null : edges.ToArray();
    }

    /// <summary>Apply the edit to every listed mesh that has the channel, and hand the bytes back to the
    /// main view model to validate and save.</summary>
    [RelayCommand]
    private async Task Apply()
    {
        if (_context is not { } ctx) { Status = "No map is open."; return; }
        if (ctx.HasPendingEdits)
        { Status = "Save your pending mesh edits first — this rewrites the mapgeo from the saved bytes."; return; }

        var targets = Meshes.Where(m => m.HasUv7).Select(m => m.Index).ToList();
        if (targets.Count == 0) { Status = "No listed mesh has a Texcoord7 channel to edit."; return; }

        IsBusy = true;
        try
        {
            var scale = new Vector2((float)ScaleU, (float)ScaleV);
            var offset = new Vector2((float)OffsetU, (float)OffsetV);
            var mode = Mode;
            byte[] source = ctx.MapGeoBytes;
            var extended = ctx.ExtendedChannelMaterials;

            var (bytes, result) = await Task.Run(() =>
            {
                byte[] b = MeshUvChannelEditor.SetTexcoord7(source, targets, mode, scale, offset,
                    out var r, extended);
                return (b, r);
            });

            if (result.MeshesChanged == 0) { Status = result.Summary; return; }
            await _save(bytes, result);
            Status = result.Summary;
            Refresh();
        }
        catch (Exception ex) { Status = "Could not rewrite the second UV set: " + ex.Message; }
        finally { IsBusy = false; }
    }
}
