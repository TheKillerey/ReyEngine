using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

public sealed record MeshDetailRow(string Label, string Value, bool Emphasis = false);

/// <summary>
/// Read-only detail view of the selected MAPGEO mesh/group (M33): name, material + source, counts, bounds,
/// transform, pivot, dragon/baron visibility (+ the resolver's reason), vertex attributes, lightmap/UV
/// availability, and render flags. Populated by <see cref="MainWindowViewModel"/> on selection/filter change.
/// </summary>
public sealed partial class MeshDetailsViewModel : ViewModelBase
{
    [ObservableProperty] private bool _hasMesh;
    [ObservableProperty] private string _title = "";
    public ObservableCollection<MeshDetailRow> Rows { get; } = new();

    public void Clear() { HasMesh = false; Title = ""; Rows.Clear(); }

    public void Load(MapGeoMesh m, string? material, string? materialSource, VisibilityDiagnostic vis)
    {
        Rows.Clear();
        Title = string.IsNullOrEmpty(m.Name) ? $"Mesh #{m.Index}" : m.Name;

        void Add(string label, string value, bool em = false) => Rows.Add(new MeshDetailRow(label, value, em));

        Add("Mesh index", m.Index.ToString());
        if (!string.IsNullOrEmpty(material)) Add("Material", material!);
        if (!string.IsNullOrEmpty(materialSource)) Add("Material source", materialSource!);
        Add("Vertices", m.VertexCount.ToString("n0"));
        Add("Indices", $"{m.IndexCount:n0} ({m.IndexCount / 3:n0} tris)");

        var size = m.BoundsMax - m.BoundsMin;
        Add("Bounds min", Fmt(m.BoundsMin));
        Add("Bounds max", Fmt(m.BoundsMax));
        Add("Size", Fmt(size));
        Add("Pivot", Fmt(m.Pivot));
        Add("Position", Fmt(m.Pivot + m.Offset));
        if (m.IsMoved)
            Add("Edits", $"offset {Fmt(m.Offset)} · rot {Fmt(m.RotationDegrees)}° · scale {Fmt(m.Scale)}", em: true);

        Add("Vertex attributes", m.Attributes.Length > 0 ? string.Join(", ", m.Attributes) : "(none)");
        Add("UV0 (Texcoord0)", m.Attributes.Contains("Texcoord0") ? "yes" : "no");
        Add("Lightmap UV (Texcoord1)", m.HasLightmapUv ? "yes" : "no");
        Add("Vertex color", m.HasVertexColor ? "yes" : "no");
        Add("Normals", m.Attributes.Contains("Normal") ? "yes" : "no");
        Add("Tangents", m.Attributes.Contains("Tangent") ? "yes" : "no");
        Add("Lightmap texture", string.IsNullOrEmpty(m.StationaryLightTexture) ? "none (no baked lightmap)" : m.StationaryLightTexture!);
        Add("Render flags", string.IsNullOrEmpty(m.RenderFlags) ? "(none)" : m.RenderFlags);
        if (m.DisableBackfaceCulling) Add("Culling", "backface culling disabled (double-sided)");

        // ---- visibility ----
        Add("Dragon layers", $"{vis.FlagLabel} · mask {vis.Flags} (0b{System.Convert.ToString(vis.Flags & 0xFF, 2).PadLeft(8, '0')})");
        Add("Controller", vis.HasController
            ? $"0x{vis.ControllerHash:x8} · dragon bits {vis.ControllerDragonBits} · baron bits {vis.ControllerBaronBits}{(vis.ControllerNotVisible ? " · inverted (ParentMode 3)" : "")}"
            : "none");
        Add("Filter", $"dragon '{vis.DragonName}' · baron '{vis.BaronName}'");
        Add("Visible now", vis.Visible ? "VISIBLE" : "HIDDEN", em: true);
        Add("Reason", vis.Reason);

        HasMesh = true;
    }

    private static string Fmt(Vector3 v) => $"({v.X:0.#}, {v.Y:0.#}, {v.Z:0.#})";
}
