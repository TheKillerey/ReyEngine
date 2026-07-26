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
    private static readonly uint F_soundPersistent = HashAlgorithms.Fnv1a("soundPersistentDefault");
    private static readonly uint F_soundOnCreate = HashAlgorithms.Fnv1a("soundOnCreateDefault");
    private static readonly uint F_visibilityRadius = HashAlgorithms.Fnv1a("visibilityRadius");

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
    private static readonly uint F_particleColorTex = HashAlgorithms.Fnv1a("particleColorTexture");
    private static readonly uint F_colorLookUpX  = HashAlgorithms.Fnv1a("colorLookUpTypeX");
    private static readonly uint F_colorLookUpY  = HashAlgorithms.Fnv1a("colorLookUpTypeY");
    private static readonly uint F_birthVelocity = HashAlgorithms.Fnv1a("birthVelocity");
    private static readonly uint F_birthAccel    = HashAlgorithms.Fnv1a("birthAcceleration");
    private static readonly uint F_accel         = HashAlgorithms.Fnv1a("acceleration");
    private static readonly uint F_birthOrbital  = HashAlgorithms.Fnv1a("birthOrbitalVelocity");
    private static readonly uint F_worldAccel    = HashAlgorithms.Fnv1a("worldAcceleration");
    private static readonly uint F_birthDrag     = HashAlgorithms.Fnv1a("birthDrag");
    private static readonly uint F_drag          = HashAlgorithms.Fnv1a("drag");
    private static readonly uint F_birthRotation = HashAlgorithms.Fnv1a("birthRotation0");
    private static readonly uint F_birthRotVel0  = HashAlgorithms.Fnv1a("birthRotationalVelocity0");
    private static readonly uint F_emitterPos    = HashAlgorithms.Fnv1a("emitterPosition");
    private const uint F_spawnShape              = 0x3bf0b4ed; // SpawnShape, observed in current SR VFX
    private static readonly uint F_emitOffset    = HashAlgorithms.Fnv1a("emitOffset");
    private static readonly uint F_emitRotAxes   = HashAlgorithms.Fnv1a("emitRotationAxes");
    private static readonly uint F_emitRotAngles = HashAlgorithms.Fnv1a("emitRotationAngles");
    private static readonly uint F_direction     = HashAlgorithms.Fnv1a("isDirectionOriented");
    private static readonly uint F_texture       = HashAlgorithms.Fnv1a("texture");
    private static readonly uint F_textureMult   = HashAlgorithms.Fnv1a("textureMult");
    private static readonly uint F_texDiv        = HashAlgorithms.Fnv1a("texDiv");
    private static readonly uint F_texDivMult    = HashAlgorithms.Fnv1a("texDivMult");
    private static readonly uint F_numFrames     = HashAlgorithms.Fnv1a("numFrames");
    private static readonly uint F_randomStart   = HashAlgorithms.Fnv1a("isRandomStartFrame");
    private static readonly uint F_birthFrameRate= HashAlgorithms.Fnv1a("birthFrameRate");
    private static readonly uint F_frameRate     = HashAlgorithms.Fnv1a("frameRate");
    private static readonly uint F_birthUvScrollMult = HashAlgorithms.Fnv1a("birthUvScrollRateMult");
    private static readonly uint F_primitive     = HashAlgorithms.Fnv1a("primitive");
    private static readonly uint F_startFrame    = HashAlgorithms.Fnv1a("startFrame");
    private static readonly uint F_legacySimple  = HashAlgorithms.Fnv1a("LegacySimple");
    private static readonly uint F_legacyBirthScale = HashAlgorithms.Fnv1a("birthScale");
    private static readonly uint F_legacyScale = HashAlgorithms.Fnv1a("scale");
    private static readonly uint F_legacyBirthRotation = HashAlgorithms.Fnv1a("birthRotation");
    private static readonly uint F_legacyBirthRotVel = HashAlgorithms.Fnv1a("birthRotationalVelocity");
    // M174: `shape` (0x9dc3d926) was dead - 0 occurrences in 1,398,802 emitters. Removed.
    // Spawn-volume classes and their parameters.
    private static readonly uint C_shapeSphere   = HashAlgorithms.Fnv1a("VfxShapeSphere");
    private static readonly uint C_shapeBox      = HashAlgorithms.Fnv1a("VfxShapeBox");
    private static readonly uint C_shapeCylinder = HashAlgorithms.Fnv1a("VfxShapeCylinder");
    private static readonly uint F_shapeRadius   = HashAlgorithms.Fnv1a("radius");
    private static readonly uint F_shapeHeight   = HashAlgorithms.Fnv1a("height");
    private static readonly uint F_shapeSize     = HashAlgorithms.Fnv1a("Size");
    // M174 tier 1: fields the census found we were discarding.
    private static readonly uint F_pass          = HashAlgorithms.Fnv1a("pass");
    private static readonly uint F_alphaRef      = HashAlgorithms.Fnv1a("alphaRef");
    private static readonly uint F_colorLookUpScales  = HashAlgorithms.Fnv1a("colorLookUpScales");
    private static readonly uint F_colorLookUpOffsets = HashAlgorithms.Fnv1a("colorLookUpOffsets");
    private static readonly uint F_velocity      = HashAlgorithms.Fnv1a("velocity");
    private static readonly uint F_rotation0     = HashAlgorithms.Fnv1a("rotation0");
    private static readonly uint C_primAttachedMesh = HashAlgorithms.Fnv1a("VfxPrimitiveAttachedMesh");
    private static readonly uint C_primBeam         = HashAlgorithms.Fnv1a("VfxPrimitiveBeam");
    // M174 tier 2.3: UV transform stack.
    private static readonly uint F_birthUVOffset   = HashAlgorithms.Fnv1a("birthUVOffset");
    private static readonly uint F_uvScale         = HashAlgorithms.Fnv1a("uvScale");
    private static readonly uint F_particleUVScroll= HashAlgorithms.Fnv1a("particleUVScrollRate");
    private static readonly uint F_uvRotation      = HashAlgorithms.Fnv1a("uvRotation");
    private static readonly uint F_uvScrollClamp   = HashAlgorithms.Fnv1a("uvScrollClamp");
    private static readonly uint F_emitterUvScroll = HashAlgorithms.Fnv1a("emitterUvScrollRate");
    private static readonly uint F_texFlipU        = HashAlgorithms.Fnv1a("TextureFlipU");
    private static readonly uint F_texFlipV        = HashAlgorithms.Fnv1a("TextureFlipV");
    private static readonly uint F_particleUVRotate= HashAlgorithms.Fnv1a("particleUVRotateRate");
    private static readonly uint F_birthUvRotate   = HashAlgorithms.Fnv1a("birthUvRotateRate");
    private static readonly uint F_uvTransformCenter = HashAlgorithms.Fnv1a("uvTransformCenter");
    // M174 tier 2.14/2.15
    private static readonly uint F_period          = HashAlgorithms.Fnv1a("period");
    private static readonly uint F_timeActive      = HashAlgorithms.Fnv1a("timeActiveDuringPeriod");
    private static readonly uint F_chanceNotExist  = HashAlgorithms.Fnv1a("ChanceToNotExist");
    private static readonly uint F_variableStart   = HashAlgorithms.Fnv1a("HasVariableStartTime");
    private static readonly uint F_emitterLinger   = HashAlgorithms.Fnv1a("emitterLinger");
    // M174 (2.1) alpha erosion. Field 0xc4663005 on the emitter; struct class 0x5e842b9b.
    private static readonly uint F_alphaErosion    = HashAlgorithms.Fnv1a("alphaErosionDefinition");
    private static readonly uint F_erosionMap      = HashAlgorithms.Fnv1a("erosionMapName");
    private static readonly uint F_erosionMixer    = HashAlgorithms.Fnv1a("erosionMapChannelMixer");
    private static readonly uint F_erosionDrive    = HashAlgorithms.Fnv1a("erosionDriveCurve");
    private static readonly uint F_erosionSlice    = HashAlgorithms.Fnv1a("erosionSliceWidth");
    private static readonly uint F_erosionFeatherIn  = HashAlgorithms.Fnv1a("erosionFeatherIn");
    private static readonly uint F_erosionFeatherOut = HashAlgorithms.Fnv1a("erosionFeatherOut");
    // M175 (2.2) soft particles. Struct class 0x1daa3fb0, measured on 14,765 live structs.
    private static readonly uint F_softParticle    = HashAlgorithms.Fnv1a("softParticleParams");
    private static readonly uint F_softBeginIn     = HashAlgorithms.Fnv1a("beginIn");
    private static readonly uint F_softDeltaIn     = HashAlgorithms.Fnv1a("deltaIn");
    private static readonly uint F_softBeginOut    = HashAlgorithms.Fnv1a("beginOut");
    private static readonly uint F_softDeltaOut    = HashAlgorithms.Fnv1a("deltaOut");
    // M175 (2.6) palette. Struct class 0xa8ad8317, measured on 8,866 live structs.
    private static readonly uint F_paletteDef      = HashAlgorithms.Fnv1a("paletteDefinition");
    private static readonly uint F_paletteTexture  = HashAlgorithms.Fnv1a("paletteTexture");
    private static readonly uint F_paletteCount    = HashAlgorithms.Fnv1a("paletteCount");
    private static readonly uint F_paletteSelector = HashAlgorithms.Fnv1a("paletteSelector");
    /// <summary>Riot's own typo, kept verbatim because the hash is what matches - "pallete", one L.</summary>
    private static readonly uint F_paletteSrcMix   = HashAlgorithms.Fnv1a("palleteSrcMixColor");
    private static readonly uint F_paletteUAnim    = HashAlgorithms.Fnv1a("PaletteUAnimationCurve");
    private static readonly uint F_paletteVAnim    = HashAlgorithms.Fnv1a("PaletteVAnimationCurve");
    // M175 (2.8) depth offset. Name recovered by brute force in M174: FNV-1a("depthPushPull") == 0xcb13aff1.
    private static readonly uint F_depthPushPull   = HashAlgorithms.Fnv1a("depthPushPull");
    // M176 (2.4) force fields. Class hashes measured on 6,134 live collections:
    //   noise 0x634db850 · drag 0xe750fae2 · acceleration 0x0a94f3d4 · attraction 0x1a7617fd · orbital 0xb67aee6f
    private static readonly uint F_fieldCollection = HashAlgorithms.Fnv1a("fieldCollectionDefinition");
    private static readonly uint F_fieldNoise      = HashAlgorithms.Fnv1a("fieldNoiseDefinitions");
    private static readonly uint F_fieldDrag       = HashAlgorithms.Fnv1a("fieldDragDefinitions");
    private static readonly uint F_fieldAccel      = HashAlgorithms.Fnv1a("fieldAccelerationDefinitions");
    private static readonly uint F_fieldAttract    = HashAlgorithms.Fnv1a("fieldAttractionDefinitions");
    private static readonly uint F_fieldOrbital    = HashAlgorithms.Fnv1a("fieldOrbitalDefinitions");
    private static readonly uint F_fldAxisFraction = HashAlgorithms.Fnv1a("axisFraction");
    private static readonly uint F_fldRadius       = HashAlgorithms.Fnv1a("radius");
    private static readonly uint F_fldFrequency    = HashAlgorithms.Fnv1a("frequency");
    private static readonly uint F_fldVelocityDelta= HashAlgorithms.Fnv1a("velocityDelta");
    private static readonly uint F_fldPosition     = HashAlgorithms.Fnv1a("Position");
    private static readonly uint F_fldStrength     = HashAlgorithms.Fnv1a("strength");
    private static readonly uint F_fldAcceleration = HashAlgorithms.Fnv1a("acceleration");
    private static readonly uint F_fldDirection    = HashAlgorithms.Fnv1a("direction");
    private static readonly uint F_fldIsLocalSpace = HashAlgorithms.Fnv1a("isLocalSpace");
    // M177 (2.5) trails. Payload class VfxTrailDefinitionData = 0x00c2a390, measured on 14,755 instances.
    private static readonly uint C_primCameraTrail   = HashAlgorithms.Fnv1a("VfxPrimitiveCameraTrail");
    private static readonly uint C_primArbitraryTrail= HashAlgorithms.Fnv1a("VfxPrimitiveArbitraryTrail");
    private static readonly uint F_mTrail            = HashAlgorithms.Fnv1a("mTrail");
    private static readonly uint F_mBirthTilingSize  = HashAlgorithms.Fnv1a("mBirthTilingSize");
    private static readonly uint F_mCutoff           = HashAlgorithms.Fnv1a("mCutoff");
    private static readonly uint F_mSmoothingMode    = HashAlgorithms.Fnv1a("mSmoothingMode");
    private static readonly uint F_mMaxAddedPerFrame = HashAlgorithms.Fnv1a("mMaxAddedPerFrame");
    private static readonly uint F_distortionDefinition = HashAlgorithms.Fnv1a("distortionDefinition");
    private static readonly uint F_distortion = HashAlgorithms.Fnv1a("distortion");
    private static readonly uint F_distortionMode = HashAlgorithms.Fnv1a("distortionMode");
    private static readonly uint F_normalMapTexture = HashAlgorithms.Fnv1a("normalMapTexture");

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
    private static readonly uint F_meshName      = HashAlgorithms.Fnv1a("mMeshName");   // skinned (.skn) mesh primitive (butterflies)
    private static readonly uint F_birthUvScroll = HashAlgorithms.Fnv1a("birthUvScrollRate");
    private static readonly uint F_meshSkeleton  = 0x90595a15; // VfxMeshDefinitionData skeleton (.skl) field (observed)
    private static readonly uint F_meshAnim      = HashAlgorithms.Fnv1a("mAnimationName");

    // primitive class hashes we treat as "mesh" (billboarded as fallback)
    private static readonly uint PrimMesh = HashAlgorithms.Fnv1a("VfxPrimitiveMesh");
    private static readonly uint PrimArbitraryQuad = HashAlgorithms.Fnv1a("VfxPrimitiveArbitraryQuad");

    // M86: skin bins carry a top-level ResourceResolver object whose resourceMap links effect KEYS
    // (what animation ParticleEventData.mEffectKey hashes) to VfxSystemDefinitionData object hashes.
    private static readonly uint ResolverClass = HashAlgorithms.Fnv1a("ResourceResolver");
    private static readonly uint F_resourceMap = HashAlgorithms.Fnv1a("resourceMap");
    private static readonly uint F_mResourceMap = HashAlgorithms.Fnv1a("mResourceMap");

    /// <summary>M86: effect-key hash → VfxSystemDefinitionData object hash, from every ResourceResolver
    /// in the bin. Empty when the bin has none. Never throws.</summary>
    public static IReadOnlyDictionary<uint, uint> ExtractResourceMap(byte[] bin)
    {
        var map = new Dictionary<uint, uint>();
        BinTree tree;
        try { tree = SafeBinTree.Parse(bin); }
        catch { return map; }
        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != ResolverClass) continue;
            if (!o.Properties.TryGetValue(F_resourceMap, out var prop)
                && !o.Properties.TryGetValue(F_mResourceMap, out prop)) continue;
            if (prop is not System.Collections.IEnumerable entries || prop is BinTreeString) continue;
            foreach (var kv in entries)
            {
                var kvType = kv.GetType();
                var key = kvType.GetProperty("Key")?.GetValue(kv);
                var val = kvType.GetProperty("Value")?.GetValue(kv);
                uint kh = key switch { BinTreeHash h => h.Value, BinTreeU32 u => u.Value, _ => 0u };
                uint vh = val switch { BinTreeObjectLink ol => ol.Value, BinTreeHash h => h.Value, BinTreeU32 u => u.Value, _ => 0u };
                if (kh != 0 && vh != 0) map[kh] = vh;
            }
        }
        return map;
    }

    /// <summary>M86: the bin's linked-bin list (PROP dependency paths, e.g. the multi-skin "longname"
    /// bins that actually hold a skin's VfxSystemDefinitionData objects). Never throws.</summary>
    public static IReadOnlyList<string> ExtractDependencies(byte[] bin)
    {
        try { return SafeBinTree.Parse(bin).Dependencies; }
        catch { return Array.Empty<string>(); }
    }

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
        string? persistentSound = GetString(o.Properties, F_soundPersistent);
        string? onCreateSound = GetString(o.Properties, F_soundOnCreate);
        float radius = GetF32(o.Properties, F_visibilityRadius) ?? 0f;
        return new VfxSystemDefinition(o.PathHash, name, path, emitters, persistentSound, onCreateSound, radius);
    }

    private static VfxEmitterDefinition ParseEmitter(BinTreeStruct s)
    {
        var p = s.Properties;

        var legacy = Get(p, F_legacySimple) as BinTreeStruct;
        var legacyBirthScale = legacy is null ? null : ReadCurveF(legacy.Properties, F_legacyBirthScale);
        var birthScale = ReadCurve3(p, F_birthScale0)
            ?? (legacyBirthScale is { } lbs ? ScalarSizeCurve(lbs) : VfxCurve3.Const(Vector3.One));
        var scaleOverLife = ReadCurve3(p, F_scale0);
        if (scaleOverLife is null && legacy is not null && ReadCurveF(legacy.Properties, F_legacyScale) is { } legacyScale)
            scaleOverLife = ScalarScaleCurve(legacyScale);
        var birthRotation = ReadCurve3(p, F_birthRotation);
        if (birthRotation is null && legacy is not null && ReadCurveF(legacy.Properties, F_legacyBirthRotation) is { } legacyRotation)
            birthRotation = ScalarRotationCurve(legacyRotation);
        var birthRotationalVelocity = ReadCurve3(p, F_birthRotVel0);
        if (birthRotationalVelocity is null && legacy is not null && ReadCurveF(legacy.Properties, F_legacyBirthRotVel) is { } legacyRotVel)
            birthRotationalVelocity = ScalarRotationCurve(legacyRotVel);
        var birthColor = ReadCurve4(p, F_birthColor) ?? VfxCurve4.Const(Vector4.One);

        // M174 (1.5): VfxPrimitiveAttachedMesh and VfxPrimitiveBeam also carry a VfxMeshDefinitionData.
        // Most AttachedMesh instances (54,690 of 56,309) hold a host-model submesh MASK rather than a mesh
        // file and stay billboards; only the ~6,382 that actually name a file gain geometry here.
        p.TryGetValue(F_primitive, out var prim);
        uint primClass = prim is BinTreeStruct pc ? pc.ClassHash : 0u;
        bool meshCapablePrim = primClass == PrimMesh || primClass == C_primAttachedMesh || primClass == C_primBeam;
        bool isArbitraryQuad = primClass == PrimArbitraryQuad;
        // M47: the mesh primitive carries its .scb/.sco path (VfxMeshDefinitionData.mSimpleMeshName) or,
        // for skinned primitives (butterflies), mMeshName (.skn) + skeleton (.skl) + mAnimationName (.anm).
        string? meshPath = null, meshSkl = null, meshAnm = null;
        if (meshCapablePrim && prim is BinTreeStruct ps2 && Get(ps2.Properties, F_meshDef) is BinTreeStruct md)
        {
            meshPath = GetString(md.Properties, F_simpleMesh) ?? GetString(md.Properties, F_meshName);
            meshSkl = GetString(md.Properties, F_meshSkeleton);
            meshAnm = GetString(md.Properties, F_meshAnim);
        }
        // Only claim the mesh path when a file was actually named - an AttachedMesh carrying just a
        // submesh mask must keep billboarding rather than render nothing.
        bool isMesh = primClass == PrimMesh || !string.IsNullOrEmpty(meshPath);

        string? textureMultPath = null;
        Vector2 textureMultTexDiv = Vector2.One, textureMultUvScroll = Vector2.Zero;
        if (Get(p, F_textureMult) is BinTreeStruct textureMult)
        {
            textureMultPath = GetString(textureMult.Properties, F_textureMult);
            textureMultTexDiv = ReadValueVec2(Get(textureMult.Properties, F_texDivMult)) ?? Vector2.One;
            textureMultUvScroll = ReadValueVec2(Get(textureMult.Properties, F_birthUvScrollMult)) ?? Vector2.Zero;
        }

        VfxDistortionDefinition? distortion = null;
        if (Get(p, F_distortionDefinition) is BinTreeStruct distortionData)
        {
            var dp = distortionData.Properties;
            distortion = new VfxDistortionDefinition(
                GetF32(dp, F_distortion) ?? 0f,
                GetU8(dp, F_distortionMode) ?? 0,
                GetString(dp, F_normalMapTexture));
        }

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
            ScaleOverLife: scaleOverLife,
            BirthColor: birthColor,
            ColorOverLife: ReadCurve4(p, F_color),
            BirthVelocity: ReadCurve3(p, F_birthVelocity),
            Acceleration: ReadCurve3(p, F_worldAccel),
            BirthRotationalVelocity: birthRotationalVelocity,
            // M174 (1.8, corrected): keep the whole value. It was reduced to `.Constant`, which threw away
            // the probability tables that are the entire point of the field on 37,220 of the 186,374
            // emitters that author it.
            EmitterPosition: ReadCurve3(p, F_emitterPos) ?? VfxCurve3.Const(Vector3.Zero),
            TexturePath: GetString(p, F_texture),
            TexDiv: GetVec2(p, F_texDiv) ?? Vector2.One,
            NumFrames: GetU16(p, F_numFrames) ?? 1,
            RandomStartFrame: GetBool(p, F_randomStart),
            IsMeshPrimitive: isMesh,
            MeshPath: meshPath,
            UvScrollRate: ReadValueVec2(Get(p, F_birthUvScroll)) ?? Vector2.Zero,
            MeshSkeletonPath: meshSkl,
            MeshAnimationPath: meshAnm,
            SpawnShape: ReadSpawnShape(p),
            BirthAcceleration: ReadCurve3(p, F_birthAccel) ?? ReadCurve3(p, F_accel),
            BirthOrbitalVelocity: ReadCurve3(p, F_birthOrbital),
            BirthDrag: ReadCurve3(p, F_birthDrag),
            DragOverLife: ReadCurve3(p, F_drag),
            BirthRotation: birthRotation,
            IsDirectionOriented: GetBool(p, F_direction),
            IsArbitraryQuad: isArbitraryQuad,
            BirthFrameRate: ReadCurveF(p, F_birthFrameRate),
            FrameRate: GetF32(p, F_frameRate),
            TextureMultPath: textureMultPath,
            TextureMultTexDiv: textureMultTexDiv,
            TextureMultUvScrollRate: textureMultUvScroll,
            StartFrame: GetU16(p, F_startFrame) ?? 0,
            UseTextureAspect: legacy is not null,
            Distortion: distortion,
            ParticleColorTexturePath: GetString(p, F_particleColorTex),
            ColorLookUpTypeX: GetU8(p, F_colorLookUpX),
            ColorLookUpTypeY: GetU8(p, F_colorLookUpY),
            Pass: GetI16(p, F_pass) ?? 0,
            AlphaRef: GetU8(p, F_alphaRef) ?? 0,
            ColorLookUpScale: GetVec2(p, F_colorLookUpScales) ?? Vector2.One,
            ColorLookUpOffset: GetVec2(p, F_colorLookUpOffsets) ?? Vector2.Zero,
            VelocityOverLife: ReadCurve3(p, F_velocity),
            RotationOverLife: ReadCurve3(p, F_rotation0),
            UvOffset: ReadValueVec2(Get(p, F_birthUVOffset)) ?? Vector2.Zero,
            UvScale: ReadValueVec2(Get(p, F_uvScale)) ?? Vector2.One,
            UvScrollIntegrated: ReadValueVec2(Get(p, F_particleUVScroll)) ?? Vector2.Zero,
            UvRotation: ReadCurveF(p, F_uvRotation)?.Constant ?? 0f,
            UvScrollClamp: GetBool(p, F_uvScrollClamp),
            EmitterUvScrollRate: GetVec2(p, F_emitterUvScroll) ?? Vector2.Zero,
            UvFlipU: GetBool(p, F_texFlipU),
            UvFlipV: GetBool(p, F_texFlipV),
            UvRotateIntegrated: ReadCurveF(p, F_particleUVRotate)?.Constant ?? 0f,
            UvRotateRate: ReadCurveF(p, F_birthUvRotate)?.Constant ?? 0f,
            UvTransformCenter: GetVec2(p, F_uvTransformCenter) ?? new Vector2(0.5f, 0.5f),
            Period: GetOptionalF32(p, F_period),
            TimeActiveDuringPeriod: GetOptionalF32(p, F_timeActive),
            ChanceToNotExist: GetF32(p, F_chanceNotExist) ?? 0f,
            HasVariableStartTime: GetBool(p, F_variableStart),
            EmitterLinger: GetOptionalF32(p, F_emitterLinger),
            AlphaErosion: ReadAlphaErosion(p),
            SoftParticle: ReadSoftParticle(p),
            Palette: ReadPalette(p),
            DepthPushPull: ReadScalar(p, F_depthPushPull),
            ForceFields: ReadForceFields(p),
            Trail: ReadTrail(prim),
            IsArbitraryTrail: primClass == C_primArbitraryTrail);
    }

    /// <summary>M177 (2.5): the trail ribbon's parameters. See VfxTrailDefinition for what the payload
    /// turned out to contain and why that settles the "is it really a ribbon" question.</summary>
    private static VfxTrailDefinition? ReadTrail(BinTreeProperty? primitive)
    {
        if (primitive is not BinTreeStruct prim) return null;
        if (prim.ClassHash != C_primCameraTrail && prim.ClassHash != C_primArbitraryTrail) return null;
        if (Get(prim.Properties, F_mTrail) is not BinTreeStruct t) return null;
        var tp = t.Properties;
        // mBirthTilingSize is a ValueVector3 but only X carries a length in the corpus.
        float tiling = (ReadValueVec3OrZero(Get(tp, F_mBirthTilingSize))).X;
        return new VfxTrailDefinition(
            tiling,
            GetF32(tp, F_mCutoff) ?? 0f,
            GetU8(tp, F_mSmoothingMode) ?? 1,
            GetI32(tp, F_mMaxAddedPerFrame) ?? 0);
    }

    private static Vector3 ReadValueVec3OrZero(BinTreeProperty? p) => p switch
    {
        BinTreeStruct value => AsVec3(Get(value.Properties, F_constantValue)) ?? Vector3.Zero,
        _ => AsVec3(p) ?? Vector3.Zero,
    };

    /// <summary>M176 (2.4): the force-field collection. See VfxForceFields for what is measured and what
    /// is inferred. Each of the five lists is a container of structs; an absent container is simply an
    /// empty list, and a collection with nothing in it at all resolves to null so the simulator can skip
    /// the whole stage with one null check.</summary>
    private static VfxForceFields? ReadForceFields(IReadOnlyDictionary<uint, BinTreeProperty> p)
    {
        if (Get(p, F_fieldCollection) is not BinTreeStruct coll) return null;
        var cp = coll.Properties;

        var noise = new List<VfxNoiseField>();
        var drag = new List<VfxDragField>();
        var accel = new List<VfxAccelerationField>();
        var attract = new List<VfxAttractionField>();
        var orbital = new List<VfxOrbitalField>();

        foreach (var f in Structs(cp, F_fieldNoise))
            noise.Add(new VfxNoiseField(
                // axisFraction is a RAW Vector3 here, not a Value* wrapper - measured on 4,635 fields.
                // Absent means "affect all axes", not "affect none": defaulting it to zero would disable
                // the commonest field type in the game.
                GetVec3(f, F_fldAxisFraction) ?? Vector3.One,
                ReadFieldScalar(f, F_fldRadius),
                ReadFieldScalar(f, F_fldFrequency),
                ReadFieldScalar(f, F_fldVelocityDelta),
                ReadFieldVec3(Get(f, F_fldPosition))));

        foreach (var f in Structs(cp, F_fieldDrag))
            drag.Add(new VfxDragField(
                ReadFieldScalar(f, F_fldRadius),
                ReadFieldScalar(f, F_fldStrength),
                ReadFieldVec3(Get(f, F_fldPosition))));

        foreach (var f in Structs(cp, F_fieldAccel))
            accel.Add(new VfxAccelerationField(
                ReadFieldVec3(Get(f, F_fldAcceleration)),
                GetBool(f, F_fldIsLocalSpace)));

        foreach (var f in Structs(cp, F_fieldAttract))
            attract.Add(new VfxAttractionField(
                ReadFieldScalar(f, F_fldRadius),
                // SIGNED - negative is a repulsor, and 10,000-magnitude negatives are authored. Do not
                // clamp this to positive.
                ReadFieldScalar(f, F_fldAcceleration),
                ReadFieldVec3(Get(f, F_fldPosition))));

        foreach (var f in Structs(cp, F_fieldOrbital))
            orbital.Add(new VfxOrbitalField(
                ReadFieldVec3(Get(f, F_fldDirection)),
                GetBool(f, F_fldIsLocalSpace)));

        var fields = new VfxForceFields(noise, drag, accel, attract, orbital);
        return fields.IsEmpty ? null : fields;
    }

    /// <summary>M176: a force-field scalar. Prefers the authored constant, and falls back to the first key
    /// of the curve when there is no constant at all.
    ///
    /// That fallback is not cosmetic. Some fields are authored purely as animated curves - measured on the
    /// live corpus, 65 of 191 orbital `direction` values carry `dynamics` while only 56 carry a
    /// `constantValue` - and taking the constant alone resolved those to zero, which silently switched the
    /// field off entirely. Reading the first key gives the authored magnitude instead of nothing.
    ///
    /// UNKNOWN: what a field curve is parameterised over - particle age, emitter age, or something else.
    /// That is why this reads one key rather than sampling over life: for the majority of fields, which
    /// are constant, the question does not arise, and for the rest a fixed authored value is closer than
    /// zero without inventing a time base.</summary>
    private static float ReadFieldScalar(IReadOnlyDictionary<uint, BinTreeProperty> props, uint field)
    {
        if (ReadCurveF(props, field) is not { } curve) return GetF32(props, field) ?? 0f;
        return curve.Constant != 0f ? curve.Constant : curve.Sample(0f);
    }

    /// <summary>M176: a force-field vector, with the same constant-then-first-key rule as
    /// <see cref="ReadFieldScalar"/>.</summary>
    private static Vector3 ReadFieldVec3(BinTreeProperty? p)
    {
        if (p is not BinTreeStruct value) return AsVec3(p) ?? Vector3.Zero;
        if (AsVec3(Get(value.Properties, F_constantValue)) is { } c && c != Vector3.Zero) return c;
        return ReadCurve3Property(p)?.Sample(0f) ?? Vector3.Zero;
    }

    private static IEnumerable<IReadOnlyDictionary<uint, BinTreeProperty>> Structs(
        IReadOnlyDictionary<uint, BinTreeProperty> props, uint field)
    {
        if (Get(props, field) is not BinTreeContainer c) yield break;
        foreach (var el in c.Elements)
            if (el is BinTreeStruct s) yield return s.Properties;
    }


    /// <summary>M174 (1.1): dispatch on the shape CLASS, not just on emitOffset.
    ///
    /// Measured over the live corpus: VfxShapeCylinder (83,984), VfxShapeBox (83,530) and VfxShapeSphere
    /// (49,305) never carry an emitOffset, so reading only that field collapsed all 216,819 of them to a
    /// single point. The other two classes - VfxShapeLegacy and the unresolved 0xee39916f, 69.4% of shapes
    /// between them - genuinely are offset-only and keep working exactly as before.</summary>
    private static VfxSpawnShape? ReadSpawnShape(IReadOnlyDictionary<uint, BinTreeProperty> emitterProps)
    {
        if (Get(emitterProps, F_spawnShape) is not BinTreeStruct shape) return null;

        var sp = shape.Properties;
        var offset = ReadCurve3Property(Get(sp, F_emitOffset)) ?? VfxCurve3.Const(Vector3.Zero);
        var axes = ReadVector3Container(Get(sp, F_emitRotAxes));
        var angles = ReadCurveFContainer(Get(sp, F_emitRotAngles));

        var kind = VfxShapeKind.Offset;
        float radius = 0f, height = 0f;
        var size = Vector3.Zero;

        if (shape.ClassHash == C_shapeSphere)
        {
            kind = VfxShapeKind.Sphere;
            radius = ReadScalar(sp, F_shapeRadius);
        }
        else if (shape.ClassHash == C_shapeCylinder)
        {
            kind = VfxShapeKind.Cylinder;
            radius = ReadScalar(sp, F_shapeRadius);
            height = ReadScalar(sp, F_shapeHeight);
        }
        else if (shape.ClassHash == C_shapeBox)
        {
            kind = VfxShapeKind.Box;
            size = ReadCurve3Property(Get(sp, F_shapeSize))?.Constant ?? GetVec3(sp, F_shapeSize) ?? Vector3.Zero;
        }

        return new VfxSpawnShape(offset, axes, angles, kind, radius, height, size);
    }

    /// <summary>M174 (2.1): the dissolve stage. See VfxAlphaErosion for the decoded shader maths.</summary>
    private static VfxAlphaErosion? ReadAlphaErosion(IReadOnlyDictionary<uint, BinTreeProperty> p)
    {
        if (Get(p, F_alphaErosion) is not BinTreeStruct e) return null;
        var ep = e.Properties;

        // The mixer is a ValueColor; its constant is what the shader dots against. Default to the red
        // channel, which is what ~74% of the corpus authors explicitly.
        var mixer = ReadCurve4(ep, F_erosionMixer)?.Constant ?? new Vector4(1f, 0f, 0f, 0f);
        var drive = ReadCurveF(ep, F_erosionDrive);
        var mapPath = GetString(ep, F_erosionMap);

        // No map and no drive means the struct carries nothing usable - skip rather than bind a null
        // sampler and multiply the sprite away.
        if (string.IsNullOrEmpty(mapPath) || drive is null) return null;

        return new VfxAlphaErosion(
            mapPath, mixer, drive.Value,
            ReadScalar(ep, F_erosionSlice),
            ReadScalar(ep, F_erosionFeatherIn),
            ReadScalar(ep, F_erosionFeatherOut));
    }

    /// <summary>M175 (2.2): the soft-particle fade. See VfxSoftParticle for the decoded and validated
    /// shader maths. All four components are plain F32 in the live data (no Value* wrappers), but
    /// ReadScalar handles both, so a wrapped variant elsewhere in the corpus still parses.</summary>
    private static VfxSoftParticle? ReadSoftParticle(IReadOnlyDictionary<uint, BinTreeProperty> p)
    {
        if (Get(p, F_softParticle) is not BinTreeStruct s) return null;
        var sp = s.Properties;
        var soft = new VfxSoftParticle(
            ReadScalar(sp, F_softBeginIn),
            ReadScalar(sp, F_softDeltaIn),
            ReadScalar(sp, F_softBeginOut),
            ReadScalar(sp, F_softDeltaOut));
        // An all-zero struct packs to a fade of 0 everywhere, which would erase the sprite. 888 of the
        // 14,765 live structs author no deltaIn at all, so this is not a hypothetical.
        return soft.IsDegenerate ? null : soft;
    }

    /// <summary>M175 (2.6): the palette lookup. See VfxPalette for the decoded and validated shader maths
    /// and for which parts of the packing remain inferred.</summary>
    private static VfxPalette? ReadPalette(IReadOnlyDictionary<uint, BinTreeProperty> p)
    {
        if (Get(p, F_paletteDef) is not BinTreeStruct s) return null;
        var sp = s.Properties;

        var texture = GetString(sp, F_paletteTexture);
        int count = GetI32(sp, F_paletteCount) ?? 0;

        // paletteSelector is a ValueVector3 whose .x is the row index; y/z are always zero in the corpus.
        float selector = ReadCurve3Property(Get(sp, F_paletteSelector))?.Constant.X
                         ?? GetVec3(sp, F_paletteSelector)?.X ?? 0f;

        // Absent in ~70% of the corpus. Luma is the default here - see VfxPalette's remarks; on the
        // greyscale source art these emitters usually use, luma and red-only are identical anyway.
        var mixer = ReadCurve4(sp, F_paletteSrcMix)?.Constant ?? new Vector4(0.3f, 0.59f, 0.11f, 0f);

        var palette = new VfxPalette(texture, count, selector, mixer,
            ReadCurveF(sp, F_paletteUAnim), ReadCurveF(sp, F_paletteVAnim));
        return palette.IsUsable ? palette : null;
    }

    /// <summary>A shape dimension, which may be authored either as a bare F32 or wrapped in a Value* struct.</summary>
    private static float ReadScalar(IReadOnlyDictionary<uint, BinTreeProperty> props, uint field)
        => ReadCurveF(props, field)?.Constant ?? GetF32(props, field) ?? 0f;

    private static VfxCurve3 ScalarSizeCurve(VfxCurveF curve) => new(
        new Vector3(curve.Constant, curve.Constant, 0f), curve.Times,
        curve.Values?.Select(static v => new Vector3(v, v, 0f)).ToArray());

    private static VfxCurve3 ScalarScaleCurve(VfxCurveF curve) => new(
        new Vector3(curve.Constant, curve.Constant, 1f), curve.Times,
        curve.Values?.Select(static v => new Vector3(v, v, 1f)).ToArray());

    private static VfxCurve3 ScalarRotationCurve(VfxCurveF curve) => new(
        new Vector3(curve.Constant, 0f, 0f), curve.Times,
        curve.Values?.Select(static v => new Vector3(v, 0f, 0f)).ToArray());

    private static IReadOnlyList<Vector3> ReadVector3Container(BinTreeProperty? prop)
    {
        if (prop is not BinTreeContainer c || c.Elements.Count == 0) return Array.Empty<Vector3>();
        var values = new List<Vector3>(c.Elements.Count);
        foreach (var el in c.Elements)
            if (AsVec3(el) is { } value) values.Add(value);
        return values;
    }

    private static IReadOnlyList<VfxCurveF> ReadCurveFContainer(BinTreeProperty? prop)
    {
        if (prop is not BinTreeContainer c || c.Elements.Count == 0) return Array.Empty<VfxCurveF>();
        var values = new List<VfxCurveF>(c.Elements.Count);
        foreach (var el in c.Elements)
            if (ReadCurveFProperty(el) is { } value) values.Add(value);
        return values;
    }

    // ---- Value* curve readers (constantValue + optional dynamics{times,values}) ----

    private static VfxCurveF? ReadCurveF(IReadOnlyDictionary<uint, BinTreeProperty> p, uint field)
    {
        return p.TryGetValue(field, out var prop) ? ReadCurveFProperty(prop) : null;
    }

    private static VfxCurveF? ReadCurveFProperty(BinTreeProperty? prop)
    {
        if (prop is BinTreeStruct v)
        {
            float c = AsF32(Get(v.Properties, F_constantValue)) ?? 0f;
            var (times, vals) = ReadDynamics(v.Properties, AsF32);
            return new VfxCurveF(c, times, vals, ReadNestedProbTables(v.Properties));
        }
        return AsF32(prop) is { } scalar ? VfxCurveF.Const(scalar) : null;
    }

    private static VfxCurve3? ReadCurve3(IReadOnlyDictionary<uint, BinTreeProperty> p, uint field)
    {
        return p.TryGetValue(field, out var prop) ? ReadCurve3Property(prop) : null;
    }

    private static VfxCurve3? ReadCurve3Property(BinTreeProperty? prop)
    {
        if (prop is BinTreeStruct v)
        {
            var c = AsVec3(Get(v.Properties, F_constantValue)) ?? Vector3.Zero;
            var (times, vals) = ReadDynamics(v.Properties, AsVec3);
            return new VfxCurve3(c, times, vals, ReadNestedProbTables(v.Properties));
        }
        return AsVec3(prop) is { } vector ? VfxCurve3.Const(vector) : null;
    }

    private static VfxCurve4? ReadCurve4(IReadOnlyDictionary<uint, BinTreeProperty> p, uint field)
    {
        if (p.TryGetValue(field, out var prop) && prop is BinTreeStruct v)
        {
            var c = AsVec4(Get(v.Properties, F_constantValue)) ?? Vector4.One;
            var (times, vals) = ReadDynamics(v.Properties, AsVec4);
            return new VfxCurve4(c, times, vals, ReadNestedProbTables(v.Properties));
        }
        return null;
    }

    /// <summary>M47: read a Value* struct's probabilityTables (container of VfxProbabilityTableData
    /// { keyTimes:[f32], keyValues:[f32] }, one per component) - Riot's exact per-particle randomisation.
    /// Null when the authored value has no per-particle probability multiplier.</summary>
    private static VfxProbTable[]? ReadProbTables(IReadOnlyDictionary<uint, BinTreeProperty> valueProps)
    {
        if (Get(valueProps, F_probTables) is not BinTreeContainer pc || pc.Elements.Count == 0) return null;
        var tables = new VfxProbTable[pc.Elements.Count];
        bool any = false;
        for (int tableIndex = 0; tableIndex < pc.Elements.Count; tableIndex++)
        {
            var el = pc.Elements[tableIndex];
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
            tables[tableIndex] = new VfxProbTable(times, vals);
            any = true;
        }
        return any ? tables : null;
    }

    /// <summary>Current VFX stores probabilityTables below dynamics. Accept the old top-level layout too.</summary>
    private static VfxProbTable[]? ReadNestedProbTables(IReadOnlyDictionary<uint, BinTreeProperty> valueProps)
    {
        if (Get(valueProps, F_dynamics) is BinTreeStruct dynamics &&
            ReadProbTables(dynamics.Properties) is { } nested)
            return nested;
        return ReadProbTables(valueProps);
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

    /// <summary>M175: `paletteCount` is I32 in the live data - and authors -1 and 0, which is why the
    /// caller has to treat it as "cannot divide by this" rather than trusting it.</summary>
    private static int? GetI32(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        => Get(p, hash) is BinTreeI32 v ? v.Value : null;

    private static Vector2? GetVec2(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        => Get(p, hash) is BinTreeVector2 v ? v.Value : null;

    private static Vector3? GetVec3(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        => Get(p, hash) is BinTreeVector3 v ? v.Value : null;

    /// <summary>M174: `pass` is I16, the only signed-16 field the resolver reads. 37,697 emitters author
    /// it as -1, so the sign matters.</summary>
    private static int? GetI16(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        => Get(p, hash) is BinTreeI16 v ? v.Value : null;

    /// <summary>Read either a plain Vector2 or a ValueVector2's authored constant.</summary>
    private static Vector2? ReadValueVec2(BinTreeProperty? p) => p switch
    {
        BinTreeStruct value => AsVec2(Get(value.Properties, F_constantValue)),
        _ => AsVec2(p),
    };

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

    private static Vector2? AsVec2(BinTreeProperty? p) => p switch
    {
        BinTreeVector2 v => v.Value,
        BinTreeVector3 v => new Vector2(v.Value.X, v.Value.Y),
        BinTreeF32 f => new Vector2(f.Value),
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
