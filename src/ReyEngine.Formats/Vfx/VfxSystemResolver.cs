using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Vfx;

/// <summary>
/// Parses <c>VfxSystemDefinitionData</c> objects out of a map's companion .materials.bin (M36) and exposes
/// them keyed by object path-hash, so a <c>MapParticle.system</c> link resolves straight to a playable
/// <see cref="VfxSystemDefinition"/>. Never throws — malformed systems are skipped.
/// </summary>
public static class VfxSystemResolver
{
    // class hashes
    private static readonly uint SystemClass  = HashAlgorithms.Fnv1a("VfxSystemDefinitionData");
    private static readonly uint EmitterClass = HashAlgorithms.Fnv1a("VfxEmitterDefinitionData");

    // system fields
    private static readonly uint F_particleName = HashAlgorithms.Fnv1a("particleName");
    private static readonly uint F_particlePath = HashAlgorithms.Fnv1a("particlePath");

    // emitter fields
    private static readonly uint F_emitterName   = HashAlgorithms.Fnv1a("emitterName");
    private static readonly uint F_rate          = HashAlgorithms.Fnv1a("rate");
    private static readonly uint F_particleLife  = HashAlgorithms.Fnv1a("particleLifetime");
    private static readonly uint F_lifetime      = HashAlgorithms.Fnv1a("lifetime");
    private static readonly uint F_particleLinger= HashAlgorithms.Fnv1a("particleLinger");
    private static readonly uint F_timeBefore    = HashAlgorithms.Fnv1a("timeBeforeFirstEmission");
    private static readonly uint F_isSingle      = HashAlgorithms.Fnv1a("isSingleParticle");
    private static readonly uint F_disabled      = HashAlgorithms.Fnv1a("disabled");
    private static readonly uint F_blendMode     = HashAlgorithms.Fnv1a("blendMode");
    private static readonly uint F_birthScale0   = HashAlgorithms.Fnv1a("birthScale0");
    private static readonly uint F_scale0        = HashAlgorithms.Fnv1a("scale0");
    private static readonly uint F_birthColor    = HashAlgorithms.Fnv1a("birthColor");
    private static readonly uint F_color         = HashAlgorithms.Fnv1a("color");
    private static readonly uint F_birthVelocity = HashAlgorithms.Fnv1a("birthVelocity");
    private static readonly uint F_worldAccel    = HashAlgorithms.Fnv1a("worldAcceleration");
    private static readonly uint F_birthRotVel0  = HashAlgorithms.Fnv1a("birthRotationalVelocity0");
    private static readonly uint F_emitterPos    = HashAlgorithms.Fnv1a("emitterPosition");
    private static readonly uint F_texture       = HashAlgorithms.Fnv1a("texture");
    private static readonly uint F_texDiv        = HashAlgorithms.Fnv1a("texDiv");
    private static readonly uint F_numFrames     = HashAlgorithms.Fnv1a("numFrames");
    private static readonly uint F_randomStart   = HashAlgorithms.Fnv1a("isRandomStartFrame");
    private static readonly uint F_primitive     = HashAlgorithms.Fnv1a("primitive");

    // Value* / dynamics inner fields
    private static readonly uint F_constantValue = HashAlgorithms.Fnv1a("constantValue");
    private static readonly uint F_dynamics      = HashAlgorithms.Fnv1a("dynamics");
    private static readonly uint F_times         = HashAlgorithms.Fnv1a("times");
    private static readonly uint F_values        = HashAlgorithms.Fnv1a("values");
    // M47: per-particle randomisation (VfxProbabilityTableData) + mesh primitive fields
    private static readonly uint F_probTables    = HashAlgorithms.Fnv1a("probabilityTables");
    private static readonly uint F_keyTimes      = HashAlgorithms.Fnv1a("keyTimes");
    private static readonly uint F_keyValues     = HashAlgorithms.Fnv1a("keyValues");
    private static readonly uint F_meshDef       = 0x0d89732d; // VfxPrimitiveMesh's VfxMeshDefinitionData field (observed)
    private static readonly uint F_simpleMesh    = HashAlgorithms.Fnv1a("mSimpleMeshName");
    private static readonly uint F_meshName      = HashAlgorithms.Fnv1a("meshName");

    // primitive class hashes we treat as "mesh" (billboarded as fallback)
    private static readonly uint PrimMesh = HashAlgorithms.Fnv1a("VfxPrimitiveMesh");

    /// <summary>Parse every VfxSystemDefinitionData in the bin, keyed by object path-hash.</summary>
    public static IReadOnlyDictionary<uint, VfxSystemDefinition> ExtractAll(byte[] materialsBin)
    {
        var map = new Dictionary<uint, VfxSystemDefinition>();
        BinTree bin;
        try { bin = SafeBinTree.Parse(materialsBin); }
        catch { return map; }

        foreach (var o in bin.Objects.Values)
        {
            if (o.ClassHash != SystemClass) continue;
            try
            {
                var sys = ParseSystem(o);
                if (sys is not null) map[o.PathHash] = sys;
            }
            catch { /* skip malformed system */ }
        }
        return map;
    }

    private static VfxSystemDefinition? ParseSystem(BinTreeObject o)
    {
        string name = GetString(o.Properties, F_particleName) ?? $"0x{o.PathHash:x8}";
        string path = GetString(o.Properties, F_particlePath) ?? "";

        var emitters = new List<VfxEmitterDefinition>();
        // Any container property whose elements are VfxEmitterDefinitionData structs holds emitters
        // (complexEmitterDefinitionData and friends) — read them all, order-preserving.
        foreach (var (_, prop) in o.Properties)
        {
            if (prop is not BinTreeContainer c) continue;
            foreach (var el in c.Elements)
                if (el is BinTreeStruct s && s.ClassHash == EmitterClass)
                    emitters.Add(ParseEmitter(s));
        }
        return new VfxSystemDefinition(o.PathHash, name, path, emitters);
    }

    private static VfxEmitterDefinition ParseEmitter(BinTreeStruct s)
    {
        var p = s.Properties;

        var birthScale = ReadCurve3(p, F_birthScale0) ?? VfxCurve3.Const(Vector3.One);
        var birthColor = ReadCurve4(p, F_birthColor) ?? VfxCurve4.Const(Vector4.One);

        bool isMesh = p.TryGetValue(F_primitive, out var prim) && prim is BinTreeStruct ps && ps.ClassHash == PrimMesh;
        // M47: the mesh primitive carries its .scb/.sco path (VfxMeshDefinitionData.mSimpleMeshName)
        string? meshPath = null;
        if (isMesh && prim is BinTreeStruct ps2 && Get(ps2.Properties, F_meshDef) is BinTreeStruct md)
            meshPath = GetString(md.Properties, F_simpleMesh) ?? GetString(md.Properties, F_meshName);

        return new VfxEmitterDefinition(
            Name: GetString(p, F_emitterName) ?? "(emitter)",
            Rate: ReadCurveF(p, F_rate) ?? VfxCurveF.Const(10f),
            ParticleLifetime: ReadCurveF(p, F_particleLife) ?? VfxCurveF.Const(1f),
            EmitterLifetime: GetOptionalF32(p, F_lifetime),
            ParticleLinger: GetOptionalF32(p, F_particleLinger) ?? 0f,
            TimeBeforeFirstEmission: GetF32(p, F_timeBefore) ?? 0f,
            IsSingleParticle: GetBool(p, F_isSingle),
            Disabled: GetBool(p, F_disabled),
            BlendMode: GetU8(p, F_blendMode) ?? 1,
            BirthScale: birthScale,
            ScaleOverLife: ReadCurve3(p, F_scale0),
            BirthColor: birthColor,
            ColorOverLife: ReadCurve4(p, F_color),
            BirthVelocity: ReadCurve3(p, F_birthVelocity),
            Acceleration: ReadCurve3(p, F_worldAccel),
            BirthRotationalVelocity: ReadCurve3(p, F_birthRotVel0),
            EmitterPosition: (ReadCurve3(p, F_emitterPos) ?? VfxCurve3.Const(Vector3.Zero)).Constant,
            TexturePath: GetString(p, F_texture),
            TexDiv: GetVec2(p, F_texDiv) ?? Vector2.One,
            NumFrames: GetU16(p, F_numFrames) ?? 1,
            RandomStartFrame: GetBool(p, F_randomStart),
            IsMeshPrimitive: isMesh,
            MeshPath: meshPath);
    }

    // ---- Value* curve readers (constantValue + optional dynamics{times,values}) ----

    private static VfxCurveF? ReadCurveF(IReadOnlyDictionary<uint, BinTreeProperty> p, uint field)
    {
        if (p.TryGetValue(field, out var prop) && prop is BinTreeStruct v)
        {
            float c = AsF32(Get(v.Properties, F_constantValue)) ?? 0f;
            var (times, vals) = ReadDynamics(v.Properties, AsF32);
            return new VfxCurveF(c, times, vals, ReadProbTables(v.Properties));
        }
        return null;
    }

    private static VfxCurve3? ReadCurve3(IReadOnlyDictionary<uint, BinTreeProperty> p, uint field)
    {
        if (p.TryGetValue(field, out var prop) && prop is BinTreeStruct v)
        {
            var c = AsVec3(Get(v.Properties, F_constantValue)) ?? Vector3.Zero;
            var (times, vals) = ReadDynamics(v.Properties, AsVec3);
            return new VfxCurve3(c, times, vals, ReadProbTables(v.Properties));
        }
        return null;
    }

    private static VfxCurve4? ReadCurve4(IReadOnlyDictionary<uint, BinTreeProperty> p, uint field)
    {
        if (p.TryGetValue(field, out var prop) && prop is BinTreeStruct v)
        {
            var c = AsVec4(Get(v.Properties, F_constantValue)) ?? Vector4.One;
            var (times, vals) = ReadDynamics(v.Properties, AsVec4);
            return new VfxCurve4(c, times, vals, ReadProbTables(v.Properties));
        }
        return null;
    }

    /// <summary>M47: read a Value* struct's probabilityTables (container of VfxProbabilityTableData
    /// { keyTimes:[f32], keyValues:[f32] }, one per component) — Riot's exact per-particle randomisation.
    /// Null when absent (most map VFX) so callers keep the constant + approximate jitter path.</summary>
    private static VfxProbTable[]? ReadProbTables(IReadOnlyDictionary<uint, BinTreeProperty> valueProps)
    {
        if (Get(valueProps, F_probTables) is not BinTreeContainer pc || pc.Elements.Count == 0) return null;
        var list = new List<VfxProbTable>(pc.Elements.Count);
        foreach (var el in pc.Elements)
        {
            if (el is not BinTreeStruct s) continue;
            if (Get(s.Properties, F_keyTimes) is not BinTreeContainer tc ||
                Get(s.Properties, F_keyValues) is not BinTreeContainer vc) continue;
            int n = Math.Min(tc.Elements.Count, vc.Elements.Count);
            if (n == 0) continue;
            var times = new float[n]; var vals = new float[n];
            for (int i = 0; i < n; i++)
            {
                times[i] = AsF32(tc.Elements[i]) ?? 0f;
                vals[i] = AsF32(vc.Elements[i]) ?? 0f;
            }
            list.Add(new VfxProbTable(times, vals));
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    /// <summary>Read a dynamics{ times:[f32], values:[T] } sub-struct into parallel arrays, or (null,null).</summary>
    private static (float[]?, T[]?) ReadDynamics<T>(IReadOnlyDictionary<uint, BinTreeProperty> valueProps, Func<BinTreeProperty?, T?> conv)
        where T : struct
    {
        if (Get(valueProps, F_dynamics) is not BinTreeStruct dyn) return (null, null);
        if (Get(dyn.Properties, F_times) is not BinTreeContainer tc) return (null, null);
        if (Get(dyn.Properties, F_values) is not BinTreeContainer vc) return (null, null);

        int n = Math.Min(tc.Elements.Count, vc.Elements.Count);
        if (n == 0) return (null, null);
        var times = new float[n];
        var vals = new T[n];
        for (int i = 0; i < n; i++)
        {
            times[i] = AsF32(tc.Elements[i]) ?? 0f;
            vals[i] = conv(vc.Elements[i]) ?? default;
        }
        return (times, vals);
    }

    // ---- primitive field getters ----

    private static BinTreeProperty? Get(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        => p.TryGetValue(hash, out var v) ? v : null;

    private static string? GetString(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        => Get(p, hash) is BinTreeString s ? s.Value : null;

    private static float? GetF32(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash) => AsF32(Get(p, hash));

    private static float? GetOptionalF32(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        => Get(p, hash) is BinTreeOptional o ? AsF32(o.Value) : AsF32(Get(p, hash));

    private static int? GetU8(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        => Get(p, hash) is BinTreeU8 u ? u.Value : null;

    private static int? GetU16(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        => Get(p, hash) is BinTreeU16 u ? u.Value : null;

    private static Vector2? GetVec2(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        => Get(p, hash) is BinTreeVector2 v ? v.Value : null;

    private static bool GetBool(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash) => Get(p, hash) switch
    {
        BinTreeBool b => b.Value,
        BinTreeBitBool bb => bb.Value,
        _ => false
    };

    // ---- primitive value coercers ----

    private static float? AsF32(BinTreeProperty? p) => p switch
    {
        BinTreeF32 f => f.Value,
        BinTreeU8 u => u.Value,
        BinTreeU16 u => u.Value,
        _ => null
    };

    private static Vector3? AsVec3(BinTreeProperty? p) => p switch
    {
        BinTreeVector3 v => v.Value,
        BinTreeVector2 v => new Vector3(v.Value, 0f),
        BinTreeF32 f => new Vector3(f.Value),
        _ => null
    };

    private static Vector4? AsVec4(BinTreeProperty? p) => p switch
    {
        BinTreeVector4 v => v.Value,
        BinTreeColor c => c.Value,
        BinTreeVector3 v => new Vector4(v.Value, 1f),
        _ => null
    };
}
