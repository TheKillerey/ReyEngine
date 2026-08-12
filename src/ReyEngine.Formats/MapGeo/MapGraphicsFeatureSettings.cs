using System.Globalization;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M436: read and edit the FIELDS of a <see cref="MapGraphicsFeatures.Feature"/> component.
///
/// <para>Field names, types and defaults come from the LeagueToolkit meta-class dump
/// (<c>data/meta/meta.db.json</c>), because the meta wiki page for MapGraphicsFeature is a stub with no
/// field documentation at all. Nothing here is guessed: a field this tool cannot describe is not offered
/// for editing.</para>
///
/// <para><b>Why fields matter and not just presence.</b> <c>MapGameplayTexture</c> declares
/// RedChannel/GreenChannel/BlueChannel/<b>AlphaChannel</b> as Link fields defaulting to <c>"0x0"</c>.
/// Adding it as a bare marker gives the engine a gameplay texture whose alpha channel points at nothing,
/// and the client fails with <c>Missing sampler "AlphaMask"</c> — confirmed in game. So the bare-marker
/// form that is correct for MapLightingV2 (map12/jade ships it with zero properties) is NOT correct in
/// general.</para>
/// </summary>
public static class MapGraphicsFeatureSettings
{
    private static readonly uint ContainerCls = HashAlgorithms.Fnv1a("MapContainer");
    private static readonly uint ComponentsField = HashAlgorithms.Fnv1a("components");

    /// <summary>One editable (or merely visible) field of a feature component.</summary>
    /// <param name="Current">The value the bin currently holds, or null when the field is absent.</param>
    /// <param name="Default">The dump's authored default, as raw JSON text.</param>
    /// <param name="Editable">False for containers, embedded structs, links and maps — shapes this
    /// editor will not invent. They are still listed so the creator can see they exist.</param>
    public sealed record Field(string Name, uint Hash, string TypeName, string? Current, string? Default, bool Editable)
    {
        public bool IsSet => Current is not null;
        public string Display => Current ?? (Default is null ? "(unset)" : $"(unset, default {Default})");
    }

    /// <summary>Describe every field the feature's class declares, with the value this bin holds.</summary>
    public static IReadOnlyList<Field> Describe(byte[] materialsBin, MapGraphicsFeatures.Feature feature,
        MetaClassDatabase? meta)
    {
        ArgumentNullException.ThrowIfNull(materialsBin);
        ArgumentNullException.ThrowIfNull(feature);

        var component = FindComponent(materialsBin, feature.Hash);
        var result = new List<Field>();
        if (meta is null || !meta.TryGetClass(feature.Hash, out var cls)) return result;

        foreach (var p in cls.Properties.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            string name = p.HasName ? p.Name : $"0x{p.Hash:x8}";
            bool editable = IsEditable(p.FieldType);
            string? current = null;
            if (component is not null && component.Properties.TryGetValue(p.Hash, out var value))
                current = Render(value);
            result.Add(new Field(name, p.Hash, p.FieldType, current, p.Default, editable));
        }
        return result;
    }

    public sealed record Result(bool Changed, string Detail);

    /// <summary>
    /// Set one field on a feature component, parsing <paramref name="text"/> according to the type the
    /// meta dump declares. An empty string CLEARS the field, which is not the same as setting it to zero:
    /// an absent field takes the engine's default, and several shipped components rely on that.
    /// </summary>
    /// <returns>The new bin bytes, or null when nothing was written.</returns>
    public static byte[]? SetField(byte[] materialsBin, MapGraphicsFeatures.Feature feature,
        uint fieldHash, string fieldType, string? text, out Result result)
    {
        ArgumentNullException.ThrowIfNull(materialsBin);
        result = new Result(false, "");

        BinTree tree;
        try { tree = new BinTree(new MemoryStream(materialsBin, false)); }
        catch (Exception ex) { result = new Result(false, $"bin did not parse: {ex.Message}"); return null; }

        foreach (var (_, obj) in tree.Objects)
        {
            if (obj.ClassHash != ContainerCls) continue;
            if (!obj.Properties.TryGetValue(ComponentsField, out var prop) || prop is not BinTreeContainer comps) continue;

            var component = comps.Elements.OfType<BinTreeStruct>().FirstOrDefault(s => s.ClassHash == feature.Hash);
            if (component is null)
            { result = new Result(false, $"{feature.Name} is not declared in this bin"); return null; }

            if (string.IsNullOrWhiteSpace(text))
            {
                if (!component.Properties.Remove(fieldHash))
                { result = new Result(false, "that field was not set, so there was nothing to clear"); return null; }
                result = new Result(true, "cleared the field (it now takes the engine default)");
            }
            else
            {
                if (!TryBuild(fieldHash, fieldType, text!, out var built, out string error))
                { result = new Result(false, error); return null; }
                component.Properties[fieldHash] = built!;
                result = new Result(true, $"set to {text.Trim()}");
            }

            var ms = new MemoryStream();
            tree.Write(ms);
            return ms.ToArray();
        }

        result = new Result(false, "no MapContainer in this bin");
        return null;
    }

    /// <summary>Only the scalar shapes are offered. Containers, embedded structs, links and maps need
    /// real content this editor has no basis to invent — inventing one is exactly how MapGameplayTexture
    /// got a null AlphaChannel.</summary>
    public static bool IsEditable(string fieldType) => fieldType switch
    {
        "Bool" or "U8" or "I8" or "U16" or "I16" or "U32" or "I32" or "F32"
            or "String" or "Vec2" or "Vec3" or "Vec4" => true,
        _ => false,
    };

    private static bool TryBuild(uint hash, string fieldType, string text, out BinTreeProperty? built, out string error)
    {
        built = null;
        error = "";
        text = text.Trim();
        var inv = CultureInfo.InvariantCulture;   // German locale: never parse floats culture-sensitively

        switch (fieldType)
        {
            case "Bool":
                if (!bool.TryParse(text, out bool b))
                {
                    if (text == "1") b = true; else if (text == "0") b = false;
                    else { error = $"'{text}' is not true/false"; return false; }
                }
                built = new BinTreeBool(hash, b); return true;

            case "U8": case "I8":
                if (!byte.TryParse(text, NumberStyles.Integer, inv, out byte u8)) { error = $"'{text}' is not a 0..255 integer"; return false; }
                built = new BinTreeU8(hash, u8); return true;

            case "U16": case "I16":
                if (!ushort.TryParse(text, NumberStyles.Integer, inv, out ushort u16)) { error = $"'{text}' is not a 0..65535 integer"; return false; }
                built = new BinTreeU16(hash, u16); return true;

            case "U32": case "I32":
                if (!uint.TryParse(text, NumberStyles.Integer, inv, out uint u32)) { error = $"'{text}' is not a non-negative integer"; return false; }
                built = new BinTreeU32(hash, u32); return true;

            case "F32":
                if (!float.TryParse(text, NumberStyles.Float, inv, out float f)) { error = $"'{text}' is not a number"; return false; }
                built = new BinTreeF32(hash, f); return true;

            case "String":
                built = new BinTreeString(hash, text); return true;

            case "Vec2":
                if (!TryVector(text, 2, out var v2)) { error = "expected 2 numbers, e.g. '1 0'"; return false; }
                built = new BinTreeVector2(hash, new System.Numerics.Vector2(v2[0], v2[1])); return true;

            case "Vec3":
                if (!TryVector(text, 3, out var v3)) { error = "expected 3 numbers, e.g. '0 1 0'"; return false; }
                built = new BinTreeVector3(hash, new System.Numerics.Vector3(v3[0], v3[1], v3[2])); return true;

            case "Vec4":
                if (!TryVector(text, 4, out var v4)) { error = "expected 4 numbers, e.g. '1 1 1 1'"; return false; }
                built = new BinTreeVector4(hash, new System.Numerics.Vector4(v4[0], v4[1], v4[2], v4[3])); return true;

            default:
                error = $"{fieldType} fields cannot be edited here — they need structured content this editor will not invent";
                return false;
        }
    }

    private static bool TryVector(string text, int count, out float[] values)
    {
        var parts = text.Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        values = new float[count];
        if (parts.Length != count) return false;
        for (int i = 0; i < count; i++)
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])) return false;
        return true;
    }

    private static string Render(BinTreeProperty p) => p switch
    {
        BinTreeBool v => v.Value ? "true" : "false",
        BinTreeU8 v => v.Value.ToString(CultureInfo.InvariantCulture),
        BinTreeU16 v => v.Value.ToString(CultureInfo.InvariantCulture),
        BinTreeU32 v => v.Value.ToString(CultureInfo.InvariantCulture),
        BinTreeF32 v => v.Value.ToString("0.######", CultureInfo.InvariantCulture),
        BinTreeString v => v.Value,
        BinTreeVector2 v => $"{v.Value.X} {v.Value.Y}",
        BinTreeVector3 v => $"{v.Value.X} {v.Value.Y} {v.Value.Z}",
        BinTreeVector4 v => $"{v.Value.X} {v.Value.Y} {v.Value.Z} {v.Value.W}",
        BinTreeObjectLink v => v.Value == 0 ? "(null link)" : $"->0x{v.Value:x8}",
        BinTreeContainer v => $"[{v.Elements.Count} element(s)]",
        BinTreeStruct v => $"<{v.ClassHash:x8}> {v.Properties.Count} field(s)",
        BinTreeMap v => $"{{{v.Count()} entr(ies)}}",
        _ => p.Type.ToString(),
    };

    private static BinTreeStruct? FindComponent(byte[] materialsBin, uint classHash)
    {
        BinTree tree;
        try { tree = new BinTree(new MemoryStream(materialsBin, false)); }
        catch { return null; }

        foreach (var (_, obj) in tree.Objects)
        {
            if (obj.ClassHash != ContainerCls) continue;
            if (!obj.Properties.TryGetValue(ComponentsField, out var prop) || prop is not BinTreeContainer comps) continue;
            var hit = comps.Elements.OfType<BinTreeStruct>().FirstOrDefault(s => s.ClassHash == classHash);
            if (hit is not null) return hit;
        }
        return null;
    }
}
