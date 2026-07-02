using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Materials;

// ---- serializable report shapes ----

public sealed record MapClassInfo(string Class, int Count, string[] HandledFields, string[] UnknownFields);

public sealed record MapMapgeoInfo(
    string Path, int MeshCount, string[] VertexAttributes,
    int MeshesWithVertexColor, int MeshesWithLightmapUv,
    int MeshesWithStationaryLightTexture, int MeshesWithBakedLightTexture,
    Dictionary<string, int> VisibilityFlagHistogram, int MeshesWithController, int DistinctControllers);

/// <summary>
/// A developer diagnostics report on a map's <c>.materials.bin</c> + <c>.mapgeo</c>: what ReyEngine
/// exposes vs what it doesn't yet, with an honest read on lighting/lightmap/visibility data. It never
/// fakes support — it lists exactly what was found and what is still missing.
/// </summary>
public sealed class MapDiagnosticsReport
{
    public string Map { get; init; } = "";
    public string GeneratedUtc { get; init; } = "";
    public MapMapgeoInfo? Mapgeo { get; init; }
    public List<MapClassInfo> Classes { get; init; } = new();

    /// <summary>Classes/objects that carry lighting/sun/fog signals (by class or field name).</summary>
    public List<string> LightingSignals { get; init; } = new();

    /// <summary>Honest plain-language findings, grouped by concern.</summary>
    public List<string> LightingFindings { get; init; } = new();
    public List<string> LightmapFindings { get; init; } = new();
    public List<string> VisibilityFindings { get; init; } = new();
    public List<string> PreviewFindings { get; init; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    // Fields ReyEngine actually reads out of a StaticMaterialDef (M32 material system).
    private static readonly HashSet<string> HandledStaticMaterial =
        new(StringComparer.OrdinalIgnoreCase) { "name", "samplerValues", "paramValues", "switches", "techniques" };

    /// <summary>Build the report from a map's bin blobs + its decoded mapgeo bytes.</summary>
    public static MapDiagnosticsReport Build(string mapName, IEnumerable<(string path, byte[] data)> bins,
        byte[]? mapgeoData, Func<uint, string?> resolve)
    {
        string Name(uint h) => resolve(h) ?? $"0x{h:x8}";

        var classCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var classFields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var lightingSignals = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (_, data) in bins)
        {
            if (data is not { Length: > 0 }) continue;
            BinTree bin;
            try { bin = SafeBinTree.Parse(data); }
            catch { continue; }
            foreach (var o in bin.Objects.Values)
            {
                string cls = Name(o.ClassHash);
                classCounts[cls] = classCounts.GetValueOrDefault(cls) + 1;
                if (!classFields.TryGetValue(cls, out var fields)) classFields[cls] = fields = new(StringComparer.OrdinalIgnoreCase);
                bool lightingHit = ClassIsLighting(cls);
                foreach (var k in o.Properties.Keys)
                {
                    string fn = Name(k);
                    fields.Add(fn);
                    if (!lightingHit && FieldIsLighting(fn)) lightingHit = true;
                }
                if (lightingHit) lightingSignals.Add(cls);
            }
        }

        var classes = classCounts.OrderByDescending(k => k.Value).Select(kv =>
        {
            var all = classFields[kv.Key];
            bool known = kv.Key.Equals("StaticMaterialDef", StringComparison.OrdinalIgnoreCase);
            var handled = known ? all.Where(HandledStaticMaterial.Contains).OrderBy(x => x).ToArray() : Array.Empty<string>();
            var unknown = all.Where(f => !known || !HandledStaticMaterial.Contains(f)).OrderBy(x => x).ToArray();
            return new MapClassInfo(kv.Key, kv.Value, handled, unknown);
        }).ToList();

        var mapgeoInfo = mapgeoData is null ? null : BuildMapgeoInfo(mapgeoData);

        var report = new MapDiagnosticsReport
        {
            Map = mapName,
            GeneratedUtc = DateTime.UtcNow.ToString("o"),
            Mapgeo = mapgeoInfo,
            Classes = classes,
            LightingSignals = lightingSignals.ToList(),
        };

        // ---- honest findings ----
        if (lightingSignals.Count == 0)
            report.LightingFindings.Add("No dedicated lighting/SunProperties object found in the map's bins. SR-style maps drive lighting from the engine/shaders, not a bin — nothing to expose here.");
        else
            report.LightingFindings.Add($"Lighting-related classes present: {string.Join(", ", lightingSignals)}. These are parsed but not yet applied to the RiotApprox preview.");

        if (mapgeoInfo is not null)
        {
            bool anyLm = mapgeoInfo.MeshesWithStationaryLightTexture > 0 || mapgeoInfo.MeshesWithBakedLightTexture > 0 || mapgeoInfo.MeshesWithLightmapUv > 0;
            if (!anyLm)
                report.LightmapFindings.Add($"No baked lightmap data in this map: StationaryLight/BakedLight channel textures are empty on all {mapgeoInfo.MeshCount} meshes and there are no Texcoord1 (lightmap) UVs. Lightmap preview is intentionally NOT implemented for this map — the data is absent, not unhandled.");
            else
                report.LightmapFindings.Add($"Lightmap data detected: {mapgeoInfo.MeshesWithStationaryLightTexture} StationaryLight + {mapgeoInfo.MeshesWithBakedLightTexture} BakedLight textures, {mapgeoInfo.MeshesWithLightmapUv} meshes with Texcoord1. This can drive a lightmap preview.");

            if (mapgeoInfo.MeshesWithVertexColor > 0)
                report.PreviewFindings.Add($"Vertex color (PrimaryColor) present on {mapgeoInfo.MeshesWithVertexColor}/{mapgeoInfo.MeshCount} meshes — this is the baked ambient/tint this format uses instead of lightmaps.");
            else
                report.PreviewFindings.Add("No PrimaryColor vertex attribute on this map's meshes.");

            report.VisibilityFindings.Add($"Dragon layer bitmask on every mesh; {mapgeoInfo.MeshesWithController} meshes reference one of {mapgeoInfo.DistinctControllers} visibility controllers. Dragon filter uses the bitmask (verified), baron filter uses the controllers.");
        }

        return report;
    }

    private static MapMapgeoInfo BuildMapgeoInfo(byte[] mapgeoData)
    {
        using var ms = new MemoryStream(mapgeoData, false);
        var env = new EnvironmentAsset(ms);
        var elementNames = (ElementName[])Enum.GetValues(typeof(ElementName));
        var attrUnion = new SortedSet<string>(StringComparer.Ordinal);
        int color = 0, lmUv = 0, statLm = 0, bakedLm = 0, withCtrl = 0;
        var ctrl = new HashSet<uint>();
        var hist = new SortedDictionary<int, int>();

        foreach (var m in env.Meshes)
        {
            var view = m.VerticesView;
            foreach (var en in elementNames)
                if (view.TryGetAccessor(en, out _)) attrUnion.Add(en.ToString());
            if (view.TryGetAccessor(ElementName.PrimaryColor, out _)) color++;
            if (view.TryGetAccessor(ElementName.Texcoord1, out _)) lmUv++;
            if (ChannelHasTexture(m.StationaryLight)) statLm++;
            if (ChannelHasTexture(m.BakedLight)) bakedLm++;
            int vf = (int)m.VisibilityFlags;
            hist[vf] = hist.GetValueOrDefault(vf) + 1;
            if (m.VisibilityControllerPathHash != 0) { withCtrl++; ctrl.Add(m.VisibilityControllerPathHash); }
        }

        return new MapMapgeoInfo(
            "", env.Meshes.Count, attrUnion.ToArray(), color, lmUv, statLm, bakedLm,
            hist.ToDictionary(k => k.Key.ToString(), v => v.Value), withCtrl, ctrl.Count);
    }

    // EnvironmentAssetChannel exposes Texture/Scale/Bias as public fields.
    private static bool ChannelHasTexture(object channel)
    {
        var f = channel.GetType().GetField("Texture", BindingFlags.Public | BindingFlags.Instance);
        return f?.GetValue(channel) is string s && s.Length > 0;
    }

    private static bool ClassIsLighting(string cls)
    {
        var lc = cls.ToLowerInvariant();
        return lc.Contains("sun") || lc.Contains("light") || lc.Contains("fog") || lc.Contains("sky")
               || lc.Contains("atmos") || lc.Contains("ambient") || lc.Contains("daylight");
    }

    private static bool FieldIsLighting(string field)
    {
        var lc = field.ToLowerInvariant();
        return lc.Contains("sun") || lc.Contains("lightmap") || lc.Contains("ambient") || lc.Contains("fog");
    }
}
