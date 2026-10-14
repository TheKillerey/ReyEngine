using System.Text.Json;

namespace ReyEngine.Formats.Interop;

/// <summary>M841: <c>assign_material</c> - draw submesh <see cref="Submesh"/> of mesh <see cref="Mesh"/> with
/// <see cref="Material"/>. A submesh of -1 means every submesh of the mesh.</summary>
public sealed record BridgeAssignment(int Mesh, int Submesh, string Material);

/// <summary>
/// M841: one <c>edit_material</c> entry. <see cref="Kind"/> is <c>param</c>, <c>sampler</c>, <c>address</c>,
/// <c>switch</c> or <c>macro</c>; <see cref="Name"/> names the parameter, sampler, switch or macro.
/// </summary>
public sealed record BridgeMaterialEdit(string Material, string Kind, string Name)
{
    /// <summary>param: the numbers (1-4).</summary>
    public IReadOnlyList<double>? Values { get; init; }
    /// <summary>param: a text value instead of numbers; sampler: the texture path (or 0x link); macro: the value.</summary>
    public string? Text { get; init; }
    /// <summary>switch: on or off.</summary>
    public bool? On { get; init; }
    /// <summary>macro: remove the define (absent is not the same as "0").</summary>
    public bool Remove { get; init; }
    /// <summary>address: the axes the request names. A null value is "not set" (Wrap).</summary>
    public IReadOnlyDictionary<char, int?> Address { get; init; } = new Dictionary<char, int?>();
}

/// <summary>M841: one refused entry of an <c>assign_material</c> or <c>edit_material</c> request.</summary>
public sealed record BridgeRefusal(int Index, string What, string Reason);

/// <summary>M841: what an edit request did.</summary>
public sealed record BridgeEditReply(int Applied, IReadOnlyList<BridgeRefusal> Refused, string Detail);

/// <summary>M841: a <c>texture</c> answer. <see cref="Bytes"/> is the file as the mounts hold it (.tex or .dds),
/// null when nothing answers the path.</summary>
public sealed record BridgeTexture(bool Found, string Path, ulong Link, string Extension, string Source, byte[]? Bytes, string? Message);

/// <summary>M841: the JSON request bodies of the Blender link, read into records. Tolerant of missing fields
/// (an entry it cannot read is skipped and counted, never thrown at the connection).</summary>
public static class BlenderLinkRequests
{
    public static IReadOnlyList<BridgeAssignment> Assignments(JsonElement request, out int unreadable)
    {
        unreadable = 0;
        var list = new List<BridgeAssignment>();
        if (!request.TryGetProperty("assignments", out var items) || items.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryInt(item, "mesh", out int mesh)
                || Str(item, "material") is not { Length: > 0 } material)
            { unreadable++; continue; }
            int submesh = TryInt(item, "submesh", out int s) ? s : -1;
            list.Add(new BridgeAssignment(mesh, submesh, material));
        }
        return list;
    }

    public static IReadOnlyList<BridgeMaterialEdit> MaterialEdits(JsonElement request, out int unreadable)
    {
        unreadable = 0;
        var list = new List<BridgeMaterialEdit>();
        if (!request.TryGetProperty("edits", out var items) || items.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || Str(item, "material") is not { Length: > 0 } material
                || Str(item, "kind") is not { Length: > 0 } kind
                || Str(item, "name") is not { Length: > 0 } name)
            { unreadable++; continue; }

            List<double>? values = null;
            string? text = null;
            if (item.TryGetProperty("value", out var value))
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.Array:
                        values = new List<double>();
                        foreach (var n in value.EnumerateArray())
                            if (n.ValueKind == JsonValueKind.Number) values.Add(n.GetDouble());
                        break;
                    case JsonValueKind.Number: values = new List<double> { value.GetDouble() }; break;
                    case JsonValueKind.String: text = value.GetString(); break;
                    case JsonValueKind.True: text = "1"; break;
                    case JsonValueKind.False: text = "0"; break;
                }
            }
            if (Str(item, "path") is { } path) text = path;
            if (Str(item, "text") is { } t) text = t;

            bool? on = item.TryGetProperty("on", out var o) && o.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? o.GetBoolean() : null;
            bool remove = (item.TryGetProperty("remove", out var r) && r.ValueKind == JsonValueKind.True)
                || (kind == "macro" && item.TryGetProperty("value", out var nv) && nv.ValueKind == JsonValueKind.Null);

            var address = new Dictionary<char, int?>();
            foreach (char axis in "uvw")
            {
                if (!item.TryGetProperty(axis.ToString(), out var a)) continue;
                address[axis] = a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out int av) && av >= 0 ? av : null;
            }

            list.Add(new BridgeMaterialEdit(material, kind, name)
            {
                Values = values, Text = text, On = on, Remove = remove, Address = address,
            });
        }
        return list;
    }

    /// <summary>A texture reference from the wire: a path, or the 0x-prefixed 16-digit form of an unnamed link.</summary>
    public static bool TryParseLink(string text, out ulong link)
    {
        link = 0;
        string t = text.Trim();
        return t.Length == 18 && t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(t.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out link);
    }

    private static bool TryInt(JsonElement item, string name, out int value)
    {
        value = 0;
        return item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value);
    }

    private static string? Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// M842: one navgrid overlay layer as the editor shows it - one per single-bit flag present
/// (<see cref="ReyEngine.Formats.MapGeo.NavGrid.PresentFlags"/>), commonest first, with the editor's label and colour.
/// </summary>
/// <param name="VisibleInEditor">Whether the editor's overlay is showing this layer right now.</param>
/// <param name="DefaultVisible">What the editor shows when its overlay is switched on with no layer chosen:
/// the commonest layer only.</param>
public sealed record BridgeNavLayer(ushort Mask, string Label, System.Numerics.Vector4 Color, int Cells,
    bool VisibleInEditor, bool DefaultVisible);

/// <summary>M842: the <c>navgrid</c> answer: the open map's navgrid and the editor's overlay layers. Read-only:
/// the link shows the navgrid, it never edits it.</summary>
public sealed record BridgeNavGrid(bool Found, string Path, string Source, ReyEngine.Formats.MapGeo.NavGrid? Grid,
    IReadOnlyList<BridgeNavLayer> Layers, string? Message)
{
    /// <summary>How far above a cell's ground height the editor draws it (world units).</summary>
    public const float Lift = 12f;
}

/// <summary>M842: the <c>navgrid</c> body.</summary>
public static class BlenderLinkNavGrid
{
    /// <summary>Body of a <c>navgrid</c> reply: f32 height per cell, then u16 flags per cell, row-major, X fastest.</summary>
    public static byte[] Encode(ReyEngine.Formats.MapGeo.NavGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        int n = grid.CellCount;
        var body = new byte[n * 6];
        for (int i = 0; i < n; i++)
        {
            float h = grid.HasHeights && grid.Heights.Length == n ? grid.Heights[i] : grid.Min.Y;
            BitConverter.TryWriteBytes(body.AsSpan(i * 4, 4), h);
            BitConverter.TryWriteBytes(body.AsSpan(n * 4 + i * 2, 2), grid.HasFlags ? grid.Flags[i] : (ushort)0);
        }
        return body;
    }
}
