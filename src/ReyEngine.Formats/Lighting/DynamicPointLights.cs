using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Baking;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Lighting;

/// <summary>One <c>MapDynamicPointLight</c> placement, in the terms the rest of the engine already uses.</summary>
public readonly record struct DynamicPointLight(
    string Name, Vector3 Position, Vector3 Color, float Radius, float IntensityScale);

/// <summary>
/// M446: read and write the modern dynamic point lights, and bridge the legacy light systems onto them.
///
/// <para><b>Why this is the bridge.</b> M196 found that <c>MapPointLight</c> PLACEMENTS are stripped from
/// every shipped map (0 of 50,107 bins) while 791 <c>MapPointLightType</c> definitions survive — so the
/// static system can describe a light but never says where one is. <c>MapDynamicPointLight</c> is the only
/// point-light class that ships with a position, and it is what the clustered forward path in
/// Mantis_Env_Baked_PBR consumes. Everything that wants real light positions goes through here.</para>
///
/// <para><b>Every structural decision below is measured, not derived from the schema.</b> The placement
/// lives in <c>MapPlaceableContainer.items</c>, which is a MAP (wire 0x86) and not a list; the value is a
/// <c>BinTreeStruct</c> (wire 0x82) and specifically NOT Embedded — placeables are the opposite of material
/// containers here, and getting that backwards is the M416 failure where the file loads everywhere and the
/// game renders nothing. The translation sits in the matrix's LAST ROW. All of it was read off Riot's only
/// two shipped instances, in map12/jade and map453/jade_container.</para>
/// </summary>
public static class DynamicPointLights
{
    /// <summary>Schema defaults, for fields a placement omits. Riot's own two lights write only transform,
    /// name, intensityScale and radius, so colour falls back to white in both.</summary>
    public const float DefaultRadius = 500f;
    public const float DefaultIntensityScale = 1f;
    public static readonly Vector3 DefaultColor = Vector3.One;

    private static uint LightClass => HashAlgorithms.Fnv1a("MapDynamicPointLight");
    private static uint ItemsField => HashAlgorithms.Fnv1a("items");
    private static uint TransformField => HashAlgorithms.Fnv1a("transform");
    private static uint NameField => HashAlgorithms.Fnv1a("name");
    private static uint IntensityField => HashAlgorithms.Fnv1a("intensityScale");
    private static uint RadiusField => HashAlgorithms.Fnv1a("radius");
    private static uint ColorField => HashAlgorithms.Fnv1a("lightColor");

    /// <summary>Every dynamic point light in a map bin. Never throws — an unreadable bin yields none.</summary>
    public static IReadOnlyList<DynamicPointLight> Read(byte[] materialsBin)
    {
        var found = new List<DynamicPointLight>();
        if (materialsBin is null || materialsBin.Length == 0) return found;

        BinTree tree;
        try { tree = SafeBinTree.Parse(materialsBin); }
        catch { return found; }

        foreach (var obj in tree.Objects.Values)
            foreach (var prop in obj.Properties.Values)
            {
                if (prop is not BinTreeMap map) continue;
                foreach (var kv in map)
                    if (kv.Value is BinTreeStruct s && s.ClassHash == LightClass)
                        found.Add(FromStruct(s));
            }
        return found;
    }

    private static DynamicPointLight FromStruct(BinTreeStruct s)
    {
        var position = Vector3.Zero;
        if (s.Properties.TryGetValue(TransformField, out var tp) && tp is BinTreeMatrix44 m)
            position = new Vector3(m.Value.M41, m.Value.M42, m.Value.M43);   // translation is the LAST ROW

        string name = s.Properties.TryGetValue(NameField, out var np) && np is BinTreeString ns ? ns.Value : "";
        float radius = s.Properties.TryGetValue(RadiusField, out var rp) && rp is BinTreeF32 rf
            ? rf.Value : DefaultRadius;
        float intensity = s.Properties.TryGetValue(IntensityField, out var ip) && ip is BinTreeF32 inf
            ? inf.Value : DefaultIntensityScale;
        var color = s.Properties.TryGetValue(ColorField, out var cp) && cp is BinTreeVector4 cv
            ? new Vector3(cv.Value.X, cv.Value.Y, cv.Value.Z) : DefaultColor;

        return new DynamicPointLight(name, position, color, radius, intensity);
    }

    /// <summary>
    /// Replace every dynamic point light in the bin with <paramref name="lights"/>.
    /// </summary>
    /// <param name="removedExisting">How many placements were dropped, so a caller can report a no-op
    /// honestly instead of implying it wrote something.</param>
    /// <returns>The rewritten bin, or null when it could not be parsed — callers keep the original.</returns>
    public static byte[]? Write(byte[] materialsBin, IEnumerable<DynamicPointLight> lights,
        out int removedExisting, out int written)
    {
        removedExisting = 0; written = 0;
        ArgumentNullException.ThrowIfNull(lights);
        if (materialsBin is null || materialsBin.Length == 0) return null;

        BinTree tree;
        try { tree = SafeBinTree.Parse(materialsBin); }
        catch { return null; }

        // Target the placeable container that already holds content, so the lights land somewhere the
        // client demonstrably loads rather than in a container of our own invention.
        BinTreeMap? target = null;
        foreach (var obj in tree.Objects.Values)
        {
            if (!obj.Properties.TryGetValue(ItemsField, out var p) || p is not BinTreeMap map) continue;
            foreach (var key in map.Where(kv => kv.Value is BinTreeStruct s && s.ClassHash == LightClass)
                                   .Select(kv => kv.Key).ToList())
            { map.Remove(key); removedExisting++; }

            if (target is null || map.Count > target.Count) target = map;
        }
        if (target is null) return null;

        uint key2 = 0x463c92ee;                       // Riot's own key in both shipped instances
        foreach (var l in lights)
        {
            while (target.Any(kv => kv.Key is BinTreeHash h && h.Value == key2)) key2++;
            target.Add(new BinTreeHash(0, key2), ToStruct(l));
            written++; key2++;
        }

        var ms = new MemoryStream();
        tree.Write(ms);
        return ms.ToArray();
    }

    private static BinTreeStruct ToStruct(DynamicPointLight l)
    {
        var m = Matrix4x4.Identity;
        m.M41 = l.Position.X; m.M42 = l.Position.Y; m.M43 = l.Position.Z;
        return new BinTreeStruct(0, LightClass, new BinTreeProperty[]
        {
            new BinTreeMatrix44(TransformField, m),
            new BinTreeString(NameField, l.Name ?? ""),
            new BinTreeF32(IntensityField, l.IntensityScale),
            new BinTreeF32(RadiusField, l.Radius),
            new BinTreeVector4(ColorField, new Vector4(l.Color, 1f)),
        });
    }

    // ---- bridges ----

    /// <summary>Hand the lights to the lightmap baker. <see cref="BakePointLight"/> and this record carry
    /// the same four quantities, so the bake and the in-game clustered path agree by construction rather
    /// than by two parallel conversions.</summary>
    public static IReadOnlyList<BakePointLight> ToBakeLights(IEnumerable<DynamicPointLight> lights)
    {
        ArgumentNullException.ThrowIfNull(lights);
        return lights.Select(l => new BakePointLight(l.Position, l.Color, l.Radius, l.IntensityScale)).ToList();
    }

    /// <summary>
    /// Convert legacy <c>Light.dat</c> lights into placements.
    /// </summary>
    /// <param name="intensityScale">
    /// <b>The one value that is NOT measured.</b> Light.dat has no intensity field at all (a line is
    /// X Y Z R G B Radius) and its editor-side <see cref="PointLight.Intensity"/> defaults to 1, whereas
    /// Riot's two shipped placements use 1000 — and on Map11, 1000 read far too bright while 5 was still
    /// "high enough". So the two systems' intensity units are NOT known to be the same, and no conversion
    /// factor here can be justified from the corpus. The default multiplies the legacy per-light intensity
    /// by 1, i.e. carries it across unchanged; tune it against a render rather than trusting it.
    /// </param>
    public static IReadOnlyList<DynamicPointLight> FromLegacy(
        IEnumerable<PointLight> legacy, string namePrefix = "PortedLight", float intensityScale = 1f)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        int i = 0;
        return legacy.Select(l => new DynamicPointLight(
            $"{namePrefix}{i++}", l.Position, l.Color, l.Radius, l.Intensity * intensityScale)).ToList();
    }
}
