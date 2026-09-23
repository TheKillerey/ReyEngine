using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>One screen fog of <see cref="MapPostFog"/>: nothing at <see cref="Start"/>, capped at
/// <see cref="MaxIntensity"/> by <see cref="End"/>.</summary>
public sealed record MapScreenFog(bool Enabled, Vector4 Color, float Start, float End, float MaxIntensity);

/// <summary>
/// M760: the map's SCREEN fog - the depth and height fog of <c>PostEffectOptions</c>, held by the unnamed
/// <c>MapGraphicsFeature</c> <c>0x50db156b</c> in the map container's <c>components</c> (its one field,
/// <c>options</c>, embeds a <c>PostEffectOptions</c> <c>0xdd3213ec</c>, per the meta schema).
///
/// <para><b>A second fog model, separate from the environment fog.</b> <c>gamma/postfog.ps</c> blob 0 runs
/// after the scene and before bloom (docs/research/frame-pipeline.md section 2.4) and reads, per pixel of
/// the depth buffer: height fog first, <c>min(saturate((world.y - start) / (end - start)), max)</c> toward
/// its colour; then depth fog, the same ramp on the distance from the camera. The sky (depth 1) is never
/// fogged. Both are off by default, and no shipped map carries the component - so on Live this draws
/// nothing, and it is here for maps that choose to use it.</para>
///
/// <para>Only the ten fog fields are read and written; the component's depth-of-field fields are left as
/// they are.</para>
/// </summary>
public sealed record MapPostFog(MapScreenFog DepthFog, MapScreenFog HeightFog)
{
    /// <summary>The schema defaults: both fogs off.</summary>
    public static MapPostFog Defaults { get; } = new(
        new MapScreenFog(false, new Vector4(0f, 0f, 0f, 1f), 5000f, 8000f, 1f),
        new MapScreenFog(false, new Vector4(0f, 0f, 0f, 1f), 300f, -100f, 1f));

    public bool DrawsAnything => DepthFog is { Enabled: true, MaxIntensity: > 0f } || HeightFog is { Enabled: true, MaxIntensity: > 0f };

    public const uint ComponentClass = 0x50db156b;
    public static readonly uint OptionsField = H("options");
    public static readonly uint OptionsClass = H("PostEffectOptions");

    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>
    /// The four parameter vectors <c>postfog.ps</c> blob 0 reads: <c>(maxIntensity, start, 1/(end - start), 0)</c>
    /// and the colour, for the depth fog and the height fog. A fog that is off, or has no range, gets a zero
    /// maximum - the shader's <c>min(..., 0)</c> then draws nothing.
    /// </summary>
    public static (Vector4 DepthParams, Vector3 DepthColor, Vector4 HeightParams, Vector3 HeightColor) ShaderParams(MapPostFog fog)
    {
        static Vector4 P(MapScreenFog f)
        {
            float span = f.End - f.Start;
            bool on = f.Enabled && MathF.Abs(span) > 1e-6f;
            return new Vector4(on ? f.MaxIntensity : 0f, f.Start, on ? 1f / span : 0f, 0f);
        }
        return (P(fog.DepthFog), new Vector3(fog.DepthFog.Color.X, fog.DepthFog.Color.Y, fog.DepthFog.Color.Z),
                P(fog.HeightFog), new Vector3(fog.HeightFog.Color.X, fog.HeightFog.Color.Y, fog.HeightFog.Color.Z));
    }

    /// <summary>The screen-fog amount blob 0 computes for one fog at <paramref name="at"/> (a height or a
    /// distance), for tests and any port.</summary>
    public static float Amount(MapScreenFog f, float at)
    {
        var p = ShaderParamsOf(f);
        return MathF.Min(Math.Clamp((at - p.Y) * p.Z, 0f, 1f), p.X);
    }

    private static Vector4 ShaderParamsOf(MapScreenFog f) => ShaderParams(new MapPostFog(f, f)).DepthParams;

    // ---------------------------------------------------------------- read

    /// <summary>The screen fog of the map container in <paramref name="materialsBin"/>; null when the map
    /// carries no post-effects component (every shipped map). A component without a field reads that
    /// field's default.</summary>
    public static MapPostFog? Extract(byte[] materialsBin)
    {
        try
        {
            var tree = SafeBinTree.Parse(materialsBin);
            foreach (var comps in Components(tree))
            {
                var component = comps.Elements.OfType<BinTreeStruct>().FirstOrDefault(e => e.ClassHash == ComponentClass);
                if (component is null) continue;
                var o = component.Properties.GetValueOrDefault(OptionsField) as BinTreeStruct;
                return new MapPostFog(Read(o, "DepthFog", Defaults.DepthFog), Read(o, "HeightFog", Defaults.HeightFog));
            }
        }
        catch { /* unparseable: none */ }
        return null;
    }

    private static MapScreenFog Read(BinTreeStruct? o, string name, MapScreenFog d)
    {
        if (o is null) return d;
        var p = o.Properties;
        bool enabled = p.GetValueOrDefault(H(name)) switch { BinTreeBool b => b.Value, BinTreeBitBool b => b.Value, _ => d.Enabled };
        return new MapScreenFog(
            enabled,
            p.GetValueOrDefault(H(name + "Color")) is BinTreeVector4 c ? c.Value : d.Color,
            p.GetValueOrDefault(H(name + "Start")) is BinTreeF32 s ? s.Value : d.Start,
            p.GetValueOrDefault(H(name + "End")) is BinTreeF32 e ? e.Value : d.End,
            p.GetValueOrDefault(H(name + "MaxIntensity")) is BinTreeF32 m ? m.Value : d.MaxIntensity);
    }

    private static IEnumerable<BinTreeContainer> Components(BinTree tree)
    {
        uint containerCls = H("MapContainer"), componentsField = H("components");
        foreach (var obj in tree.Objects.Values)
            if (obj.ClassHash == containerCls && obj.Properties.GetValueOrDefault(componentsField) is BinTreeContainer c)
                yield return c;
    }

    // ---------------------------------------------------------------- write

    /// <summary>
    /// Write <paramref name="fog"/> into the map container's post-effects component, creating the component
    /// (and its <c>options</c>) when the map has none and the fog is not all defaults. A field already present
    /// is updated; an absent one is added only when it differs from the default - the same rule as the sun
    /// writer, so a no-op edit leaves the bin byte-identical. Null when the bin has no map container.
    /// </summary>
    public static byte[]? Write(byte[] materialsBin, MapPostFog fog, out string detail)
    {
        detail = "";
        BinTree tree;
        try { tree = SafeBinTree.Parse(materialsBin); }
        catch (Exception ex) { detail = $"bin did not parse: {ex.Message}"; return null; }

        uint containerCls = H("MapContainer"), componentsField = H("components");
        foreach (var obj in tree.Objects.Values)
        {
            if (obj.ClassHash != containerCls || obj.Properties.GetValueOrDefault(componentsField) is not BinTreeContainer comps) continue;
            var component = comps.Elements.OfType<BinTreeStruct>().FirstOrDefault(e => e.ClassHash == ComponentClass);
            bool created = false;
            if (component is null)
            {
                if (fog == Defaults) { detail = "nothing to write: both fogs are at their defaults and the map has no component"; return materialsBin; }
                // the same runtime form as the container's other components, which is the form the game reads
                bool embedded = comps.Elements.FirstOrDefault() is BinTreeEmbedded;
                component = embedded
                    ? new BinTreeEmbedded(0, ComponentClass, Array.Empty<BinTreeProperty>())
                    : new BinTreeStruct(0, ComponentClass, Array.Empty<BinTreeProperty>());
                if (comps.Elements is IList<BinTreeProperty> list && !list.IsReadOnly) list.Add(component);
                else obj.Properties[componentsField] = comps is BinTreeUnorderedContainer
                    ? new BinTreeUnorderedContainer(componentsField, comps.ElementType, comps.Elements.Append(component))
                    : new BinTreeContainer(componentsField, comps.ElementType, comps.Elements.Append(component));
                created = true;
            }
            if (component.Properties.GetValueOrDefault(OptionsField) is not BinTreeStruct options)
            {
                options = new BinTreeEmbedded(OptionsField, OptionsClass, Array.Empty<BinTreeProperty>());
                component.Properties[OptionsField] = options;
            }

            int updated = 0, added = 0;
            void Set(string name, BinTreeProperty fresh, bool isDefault)
            {
                uint h = H(name);
                bool exists = options.Properties.ContainsKey(h);
                if (!exists && isDefault) return;
                options.Properties[h] = fresh;
                if (exists) updated++; else added++;
            }
            void Fog(string name, MapScreenFog f, MapScreenFog d)
            {
                Set(name, new BinTreeBool(H(name), f.Enabled), f.Enabled == d.Enabled);
                Set(name + "Color", new BinTreeVector4(H(name + "Color"), f.Color), f.Color == d.Color);
                Set(name + "Start", new BinTreeF32(H(name + "Start"), f.Start), f.Start == d.Start);
                Set(name + "End", new BinTreeF32(H(name + "End"), f.End), f.End == d.End);
                Set(name + "MaxIntensity", new BinTreeF32(H(name + "MaxIntensity"), f.MaxIntensity), f.MaxIntensity == d.MaxIntensity);
            }
            Fog("DepthFog", fog.DepthFog, Defaults.DepthFog);
            Fog("HeightFog", fog.HeightFog, Defaults.HeightFog);

            using var ms = new MemoryStream();
            tree.Write(ms);
            detail = (created ? "created the post-effects component; " : "") + $"{updated} field(s) updated, {added} added";
            return ms.ToArray();
        }
        detail = "no MapContainer with components in this bin";
        return null;
    }
}
