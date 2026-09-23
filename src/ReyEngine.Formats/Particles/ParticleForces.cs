using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Particles;

/// <summary>The five force-field kinds Riot's <c>VfxFieldCollectionDefinitionData</c> holds.</summary>
public enum ParticleForceKind { Acceleration, Drag, Noise, Orbital, Attraction }

/// <summary>One editable value of a force: a scalar, a vector, or a plain vec3.</summary>
/// <param name="Field">The bin field name (e.g. <c>radius</c>).</param>
/// <param name="Label">What the editor calls it.</param>
/// <param name="IsVector">Three components rather than one.</param>
/// <param name="Value">The constant (X only for a scalar). A value authored only as a curve reads its
/// first key, as the preview does (M176).</param>
/// <param name="HasCurve">The value carries a curve; the constant is not the whole story.</param>
/// <param name="IsAuthored">The field is in the bin; otherwise the value shown is the class default.</param>
public sealed record ParticleForceValue(string Field, string Label, bool IsVector, Vector3 Value, bool HasCurve, bool IsAuthored);

/// <summary>One force of one emitter, as the editor shows it.</summary>
public sealed record ParticleForce(ParticleForceKind Kind, int Index, IReadOnlyList<ParticleForceValue> Values)
{
    /// <summary>Drag, noise and attraction act around a point; acceleration and orbital act everywhere.</summary>
    public bool IsPositional => Kind is ParticleForceKind.Drag or ParticleForceKind.Noise or ParticleForceKind.Attraction;

    /// <summary>Where a positional force acts, relative to the SYSTEM's origin - the simulator transforms
    /// it by the system's world transform, not by the emitter's own offset.</summary>
    public Vector3 Position => Values.FirstOrDefault(v => v.Field == "Position")?.Value ?? Vector3.Zero;

    public float Radius => Values.FirstOrDefault(v => v.Field == "radius")?.Value.X ?? 0f;

    public string Title => $"{ParticleForces.Name(Kind)} {Index + 1}";
}

/// <summary>
/// M752: the force fields of one emitter, read and written on its live bin struct.
///
/// <para>The wire forms are Riot's, as M522's troybin converter already writes them after a census of
/// 5,138 shipped collections: <c>fieldCollectionDefinition</c> is a POINTER to a
/// <c>VfxFieldCollectionDefinitionData</c>; each kind is a LIST of EMBEDDED definitions; scalars are
/// <c>ValueFloat</c> and vectors <c>ValueVector3</c>, each wrapping a <c>constantValue</c>; noise's
/// <c>axisFraction</c> is a plain vec3; and a zero <c>Position</c> is omitted rather than written.</para>
///
/// <para>Riot ships no empty container and an empty one crashes the client at map load (M414), so removing
/// a kind's last force removes its list, and removing the collection's last list removes the collection.</para>
/// </summary>
public static class ParticleForces
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly uint F_collection = H("fieldCollectionDefinition");
    private static readonly uint CollectionClass = H("VfxFieldCollectionDefinitionData");
    private static readonly uint F_constant = H("constantValue");
    private static readonly uint F_dynamics = H("dynamics");
    private static readonly uint F_times = H("times");
    private static readonly uint F_values = H("values");
    private static readonly uint ValueFloatClass = H("ValueFloat");
    private static readonly uint ValueVector3Class = H("ValueVector3");

    /// <summary>List field and definition class of each kind.</summary>
    private static (string List, string Class) Wire(ParticleForceKind kind) => kind switch
    {
        ParticleForceKind.Acceleration => ("fieldAccelerationDefinitions", "VfxFieldAccelerationDefinitionData"),
        ParticleForceKind.Drag => ("fieldDragDefinitions", "VfxFieldDragDefinitionData"),
        ParticleForceKind.Noise => ("fieldNoiseDefinitions", "VfxFieldNoiseDefinitionData"),
        ParticleForceKind.Orbital => ("fieldOrbitalDefinitions", "VfxFieldOrbitalDefinitionData"),
        _ => ("fieldAttractionDefinitions", "VfxFieldAttractionDefinitionData"),
    };

    public static string Name(ParticleForceKind kind) => kind switch
    {
        ParticleForceKind.Acceleration => "Acceleration",
        ParticleForceKind.Drag => "Drag",
        ParticleForceKind.Noise => "Noise",
        ParticleForceKind.Orbital => "Orbital",
        _ => "Attraction",
    };

    /// <summary>The fields each kind carries, in the order the editor shows them. The kind decides the
    /// wire form: a vector field is a ValueVector3, <c>axisFraction</c> a plain vec3, the rest ValueFloat.</summary>
    private static IReadOnlyList<(string Field, string Label, bool IsVector)> Layout(ParticleForceKind kind) => kind switch
    {
        ParticleForceKind.Acceleration => new[] { ("acceleration", "Acceleration", true) },
        ParticleForceKind.Drag => new[] { ("strength", "Strength", false), ("radius", "Radius", false), ("Position", "Position", true) },
        ParticleForceKind.Noise => new[]
        {
            ("velocityDelta", "Strength", false), ("frequency", "Frequency", false), ("radius", "Radius", false),
            ("Position", "Position", true), ("axisFraction", "Axes", true),
        },
        ParticleForceKind.Orbital => new[] { ("direction", "Spin (rad/s per axis)", true) },
        _ => new[] { ("acceleration", "Pull (negative pushes)", false), ("radius", "Radius", false), ("Position", "Position", true) },
    };

    /// <summary>Riot's typical values, from a census of 16,302 shipped fields - so a new force does
    /// something the moment it is added. The commonest orbital value is (0,0,0), which does nothing, so
    /// the runner-up is used; noise's axes are left to the class default, all three.</summary>
    private static (string Field, Vector3 Value)[] Defaults(ParticleForceKind kind) => kind switch
    {
        ParticleForceKind.Acceleration => new[] { ("acceleration", new Vector3(0, 40, 0)) },
        ParticleForceKind.Drag => new[] { ("strength", new Vector3(2, 0, 0)), ("radius", new Vector3(1000, 0, 0)) },
        ParticleForceKind.Noise => new[]
        {
            ("radius", new Vector3(300, 0, 0)), ("velocityDelta", new Vector3(20, 0, 0)), ("frequency", new Vector3(10, 0, 0)),
        },
        ParticleForceKind.Orbital => new[] { ("direction", new Vector3(0, 1, 0)) },
        _ => new[] { ("acceleration", new Vector3(500, 0, 0)), ("radius", new Vector3(500, 0, 0)) },
    };

    // ------------------------------------------------------------------ read

    public static IReadOnlyList<ParticleForce> Read(BinTreeStruct emitter)
    {
        var result = new List<ParticleForce>();
        if (emitter.Properties.GetValueOrDefault(F_collection) is not BinTreeStruct collection) return result;
        foreach (ParticleForceKind kind in Enum.GetValues<ParticleForceKind>())
        {
            if (collection.Properties.GetValueOrDefault(H(Wire(kind).List)) is not BinTreeContainer list) continue;
            int i = 0;
            foreach (var element in list.Elements)
            {
                if (element is BinTreeStruct def)
                    result.Add(new ParticleForce(kind, i, Layout(kind).Select(l => ReadValue(def, l.Field, l.Label, l.IsVector)).ToList()));
                i++;
            }
        }
        return result;
    }

    private static ParticleForceValue ReadValue(BinTreeStruct def, string field, string label, bool isVector)
    {
        var p = def.Properties.GetValueOrDefault(H(field));
        bool authored = p is not null;
        Vector3 value = field == "axisFraction" && p is null ? Vector3.One : Vector3.Zero;
        bool curve = false;
        switch (p)
        {
            case BinTreeVector3 plain: value = plain.Value; break;
            case BinTreeStruct wrapper:
                curve = wrapper.Properties.ContainsKey(F_dynamics);
                if (wrapper.Properties.GetValueOrDefault(F_constant) is BinTreeF32 f) value = new Vector3(f.Value, 0, 0);
                else if (wrapper.Properties.GetValueOrDefault(F_constant) is BinTreeVector3 v) value = v.Value;
                else if (curve && FirstKey(wrapper) is { } first) value = first;
                break;
        }
        return new ParticleForceValue(field, label, isVector, value, curve, authored);
    }

    private static Vector3? FirstKey(BinTreeStruct wrapper)
    {
        if (wrapper.Properties.GetValueOrDefault(F_dynamics) is not BinTreeStruct dyn
            || dyn.Properties.GetValueOrDefault(F_values) is not BinTreeContainer values || values.Elements.Count == 0)
            return null;
        return values.Elements[0] switch
        {
            BinTreeF32 f => new Vector3(f.Value, 0, 0),
            BinTreeVector3 v => v.Value,
            _ => null,
        };
    }

    // ------------------------------------------------------------------ write

    /// <summary>Append a force of <paramref name="kind"/> at Riot's typical values. Returns its index.</summary>
    public static int Add(BinTreeStruct emitter, ParticleForceKind kind)
    {
        var (listName, className) = Wire(kind);
        var collection = emitter.Properties.GetValueOrDefault(F_collection) as BinTreeStruct;
        if (collection is null)
        {
            collection = new BinTreeStruct(F_collection, CollectionClass, Array.Empty<BinTreeProperty>());
            emitter.Properties[F_collection] = collection;
        }
        var def = new BinTreeEmbedded(0, H(className), Array.Empty<BinTreeProperty>());
        foreach (var (field, value) in Defaults(kind))
            WriteValue(def, field, Layout(kind).First(l => l.Field == field).IsVector, value);

        var old = collection.Properties.GetValueOrDefault(H(listName)) as BinTreeContainer;
        var elements = old?.Elements.ToList() ?? new List<BinTreeProperty>();
        elements.Add(def);
        collection.Properties[H(listName)] = Rebuilt(old, H(listName), elements);
        return elements.Count - 1;
    }

    /// <summary>Remove one force. The last one of a kind takes its list with it, and the last list takes
    /// the collection - Riot ships no empty container (M414).</summary>
    public static void Remove(BinTreeStruct emitter, ParticleForceKind kind, int index)
    {
        var (collection, list) = Locate(emitter, kind, index);
        uint listHash = H(Wire(kind).List);
        var elements = list.Elements.ToList();
        elements.RemoveAt(index);
        if (elements.Count > 0) collection.Properties[listHash] = Rebuilt(list, listHash, elements);
        else collection.Properties.Remove(listHash);
        if (collection.Properties.Count == 0) emitter.Properties.Remove(F_collection);
    }

    /// <summary>Set one value's constant. A value that also carries a curve keeps it - the curve is edited
    /// where every other curve is, in the property list.</summary>
    public static void Set(BinTreeStruct emitter, ParticleForceKind kind, int index, string field, Vector3 value)
    {
        var layout = Layout(kind).FirstOrDefault(l => l.Field == field);
        if (layout.Field is null) throw new ArgumentException($"{Name(kind)} has no field '{field}'.", nameof(field));
        var (_, list) = Locate(emitter, kind, index);
        if (list.Elements[index] is not BinTreeStruct def) throw new InvalidOperationException("The force is not a struct.");
        WriteValue(def, field, layout.IsVector, value);
    }

    private static (BinTreeStruct Collection, BinTreeContainer List) Locate(BinTreeStruct emitter, ParticleForceKind kind, int index)
    {
        if (emitter.Properties.GetValueOrDefault(F_collection) is not BinTreeStruct collection
            || collection.Properties.GetValueOrDefault(H(Wire(kind).List)) is not BinTreeContainer list
            || index < 0 || index >= list.Elements.Count)
            throw new InvalidOperationException($"This emitter has no {Name(kind)} force {index + 1}.");
        return (collection, list);
    }

    /// <summary>Keep the container's wire form - ordered or unordered - as M651 does for every list edit.</summary>
    private static BinTreeContainer Rebuilt(BinTreeContainer? old, uint name, List<BinTreeProperty> elements) =>
        old is BinTreeUnorderedContainer
            ? new BinTreeUnorderedContainer(name, old.ElementType, elements)
            : new BinTreeContainer(name, old?.ElementType ?? BinPropertyType.Embedded, elements);

    private static void WriteValue(BinTreeStruct def, string field, bool isVector, Vector3 value)
    {
        uint hash = H(field);
        if (field == "axisFraction")
        {
            def.Properties[hash] = new BinTreeVector3(hash, value);   // a plain vec3 on 3,612 of 3,635 shipped
            return;
        }
        if (field == "Position" && value == Vector3.Zero && def.Properties.GetValueOrDefault(hash) is not BinTreeStruct { } existing)
        {
            // Riot omits a zero Position rather than writing it; nothing to add.
            return;
        }
        if (def.Properties.GetValueOrDefault(hash) is BinTreeStruct wrapper)
        {
            // keep the wrapper and anything else in it (a curve, a probability table); only the constant moves
            wrapper.Properties[F_constant] = isVector ? new BinTreeVector3(F_constant, value) : new BinTreeF32(F_constant, value.X);
            return;
        }
        def.Properties[hash] = new BinTreeEmbedded(hash, isVector ? ValueVector3Class : ValueFloatClass, new BinTreeProperty[]
        {
            isVector ? new BinTreeVector3(F_constant, value) : new BinTreeF32(F_constant, value.X),
        });
    }
}
