using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M196 (tier 4.6): the two lighting classes the map bins carry and ReyEngine never read.
///
/// <para>The report bundled them as one item; measured, they are different in kind and only one of them
/// can ever be rendered. It also justified both with "affects how all lit VFX read on modern maps", which
/// is <b>not supported</b>: <c>VfxParticleRenderer</c> declares no lighting uniform, and the only
/// lighting-adjacent emitter field in the whole census is <c>doesCastShadow</c>, which nothing consumes.
/// These affect map GEOMETRY lighting, not particles.</para>
///
/// <para>This file parses both. Neither is wired into the viewport here, deliberately - tier 4 is parsing
/// support, and applying the volumes is a visible brightness change that needs the extent convention
/// settled first (see <see cref="MapLightingVolume"/>).</para>
/// </summary>
public static class MapLighting
{
    /// <summary>Read every <see cref="MapLightingVolume"/> placeable and every orphaned
    /// <see cref="MapPointLightDefinition"/> from a map materials bin. Never throws.</summary>
    public static (IReadOnlyList<MapLightingVolume> Volumes, IReadOnlyList<MapPointLightDefinition> PointLights)
        Extract(byte[] materialsBin, MapSunProperties? globalSun)
    {
        var volumes = new List<MapLightingVolume>();
        var lights = new List<MapPointLightDefinition>();
        BinTree tree;
        try { tree = SafeBinTree.Parse(materialsBin); }
        catch { return (volumes, lights); }

        uint volumeCls = HashAlgorithms.Fnv1a("MapLightingVolume");
        uint pointCls = HashAlgorithms.Fnv1a("MapPointLightType");
        uint containerCls = HashAlgorithms.Fnv1a("MapPlaceableContainer");

        foreach (var o in tree.Objects.Values)
        {
            // Point lights are TOP-LEVEL bin entries, not placeables - see the record's remarks.
            if (o.ClassHash == pointCls)
            {
                lights.Add(new MapPointLightDefinition(
                    o.PathHash,
                    Field(o.Properties, "lightColor") switch
                    {
                        BinTreeVector4 v => v.Value,
                        BinTreeColor c => new Vector4(c.Value.R, c.Value.G, c.Value.B, c.Value.A),
                        _ => null,
                    },
                    Field(o.Properties, "radius") is BinTreeF32 r ? r.Value : null,
                    Field(o.Properties, "Impact") is BinTreeU8 im ? im.Value : null,
                    Field(o.Properties, "castStaticShadows") switch
                    {
                        BinTreeBool b => b.Value,
                        BinTreeBitBool b => b.Value,
                        _ => null,
                    },
                    Field(o.Properties, "HdrScale") is BinTreeF32 hs ? hs.Value : null));
                continue;
            }

            if (o.ClassHash != containerCls) continue;
            if (Field(o.Properties, "items") is not { } items || items is not System.Collections.IEnumerable en) continue;

            foreach (var it in en)
            {
                if (it.GetType().GetProperty("Value")?.GetValue(it) is not BinTreeStruct s) continue;
                if (s.ClassHash != volumeCls) continue;

                var transform = Field(s.Properties, "transform") is BinTreeMatrix44 m ? m.Value : Matrix4x4.Identity;
                volumes.Add(new MapLightingVolume(
                    (Field(s.Properties, "name") as BinTreeString)?.Value ?? "(volume)",
                    transform,
                    // A field the volume omits inherits the bin's GLOBAL sun, NOT a neutral default.
                    // Measured: 15 of 172 volumes omit skyLightScale/horizonColor/groundColor and 12 omit
                    // sunColor, so falling back to Vector4.One would render those blown-out white.
                    ReadVolumeLighting(s, globalSun),
                    Field(s.Properties, "mVisibilityFlags") is BinTreeU8 vf ? vf.Value : 255));
            }
        }
        return (volumes, lights);
    }

    /// <summary>
    /// M207: the lighting a scene should actually render with - the bin's global sun, unless the bin has
    /// exactly ONE <see cref="MapLightingVolume"/>, in which case that volume's lighting is used for the
    /// whole scene.
    ///
    /// <para><b>Why "exactly one", and why no box test.</b> Whether a volume's transform basis is a
    /// half-extent or a full extent is NOT resolvable from the bins: the struct carries no bound of any
    /// kind (its 22 fields are transform, name, mVisibilityFlags and 19 lighting parameters), and a
    /// containment test cannot discriminate either, because the larger reading contains more points by
    /// construction. Rather than pick a factor on a coin-flip, this sidesteps the question - with one
    /// volume there is nothing to choose BETWEEN, so the only thing a box could decide is whether to fall
    /// back to a global sun that, for 146 of 172 volumes, is dimmer than the volume by up to 2x.</para>
    ///
    /// <para>Measured: 128 of the 150 bins carrying volumes have exactly one. The other 22 are left on the
    /// global sun until the extent convention is settled, because there a wrong box picks the wrong
    /// VOLUME, which is a different and worse error than a wrong boundary.</para>
    ///
    /// <para>The residual assumption, stated plainly: a lone volume covers wherever the camera goes. That
    /// is not proven. It is preferred to the status quo only because the status quo is already known to be
    /// wrong for those scenes.</para>
    /// </summary>
    /// <param name="initialVisibilityMask">M785: the map's <c>InitialVisibilityMask</c> (the shipping
    /// <c>mapNN.bin</c>'s primary axis), asked for lazily and only when <see cref="ActiveAtStart"/> needs it.
    /// Null keeps the M207 behaviour exactly.</param>
    public static MapSunProperties? EffectiveSun(byte[] materialsBin, Func<int?>? initialVisibilityMask = null)
    {
        var global = MapSunProperties.Extract(materialsBin);
        var (volumes, _) = Extract(materialsBin, global);
        if (volumes.Count == 1) return volumes[0].Lighting;
        // M785: several volumes at ONE transform - the TFT boards - are not a spatial choice at all, so M207's
        // extent question cannot bite; the visibility layer that is on at the start picks the one volume.
        if (initialVisibilityMask is not null && ActiveAtStart(volumes, initialVisibilityMask) is { } active)
            return active.Lighting;
        return global;
    }

    /// <summary>
    /// M785: the volume a multi-volume bin lights its scene with at the start of a game, or null when that is
    /// not decidable - in which case the caller stays on the global sun, as M207 does.
    ///
    /// <para><b>Only co-located volumes.</b> Every volume must carry the identical transform. Then no box can
    /// tell them apart and <c>mVisibilityFlags</c> is the only thing left that can, so M207's half- vs
    /// full-extent question does not arise. Volumes at different places are still left alone.</para>
    ///
    /// <para><b>The layer that is on at the start.</b> A volume is active when its flags share a bit with the
    /// map's InitialVisibilityMask; exactly one may be, or the answer is null (which of two wins is not
    /// known). Measured, not guessed: Map22 starts at 67 (bit 6 = "base"), and anniversary.materials.bin's
    /// two volumes sit at the same transform with flags 64 and 8. Its own MapBehaviors say how they are used -
    /// BoardReady sets the flags to 68 (base + Stage1); LevelUp7Planning fades LightingVolume1 (FNV-1a
    /// 0x7367c3b6) toward LightingVolume2's fog range (200, -1400) and then sets the flags to 8 - so the
    /// level-1 board is lit by the flag-64 volume: lightMapColorScale 2 where the global sun says 1. Of the
    /// shipped bins, 15 carry co-located volumes and 13 of them resolve to exactly one this way (all Map22
    /// arena skins).</para>
    /// </summary>
    public static MapLightingVolume? ActiveAtStart(IReadOnlyList<MapLightingVolume> volumes, Func<int?> initialVisibilityMask)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(initialVisibilityMask);
        if (!CoLocated(volumes)) return null;
        if (initialVisibilityMask() is not { } mask || mask == 0) return null;
        return OnFor(volumes, mask);
    }

    /// <summary>
    /// M797: <see cref="ActiveAtStart"/>'s rule for any visibility mask - the one a TFT board stage sets. On
    /// anniversary, BoardReady (68) turns on the flag-64 volume and level 7 (8) the flag-8 one; on
    /// dawnbringernightbringer level 7 (20) turns on the flag-16 volume and board-ready (64) the flag-64 one.
    /// Null when the volumes are not co-located, the mask is empty, or not exactly one of them is on.
    /// </summary>
    public static MapLightingVolume? ActiveForMask(IReadOnlyList<MapLightingVolume> volumes, int mask)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        if (!CoLocated(volumes) || mask == 0) return null;
        return OnFor(volumes, mask);
    }

    /// <summary>
    /// M797: the sun a scene is lit with when the visibility mask is <paramref name="mask"/> - the whole of
    /// <see cref="EffectiveSun"/>'s rule, from volumes already read: a lone volume lights the scene (M207),
    /// several co-located ones are told apart by the mask (M785), anything else keeps the global sun.
    /// </summary>
    public static MapSunProperties? SunForMask(IReadOnlyList<MapLightingVolume> volumes, MapSunProperties? global, int mask) =>
        VolumeForMask(volumes, mask)?.Lighting ?? global;

    /// <summary>
    /// M797: the volume <see cref="SunForMask"/> lights the scene with, or null when the global sun does: a lone
    /// volume (M207) or the one co-located volume the mask turns on (M785). Named separately so a caller can SAY
    /// which lit the scene - a board with one volume is lit by that volume, whatever the mask.
    /// </summary>
    public static MapLightingVolume? VolumeForMask(IReadOnlyList<MapLightingVolume> volumes, int mask)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        return volumes.Count == 1 ? volumes[0] : ActiveForMask(volumes, mask);
    }

    /// <summary>
    /// M797: what a board STAGE changes in the lighting - the nine fields a <see cref="MapLightingVolume"/> authors
    /// and <c>ReadVolumeLighting</c> models (sun colour and direction, sky colour and scale, lightmap scale, horizon
    /// and ground colour, fog colour and range) - laid over <paramref name="board"/>, every other field left as
    /// <paramref name="board"/> has it. <paramref name="lit"/> is a sun as this class reads it (a volume's, or the
    /// global one).
    ///
    /// <para><b>Why the stage does this, and <c>ReadVolumeLighting</c> does not read the volume on top of the global
    /// sun instead.</b> That reading would make a volume-lit view take the board's own fogEnabled, fog alternate
    /// colour, emissive remaps and shadow settings - more faithful to the data, but measured: it moves the START
    /// view too. 22 of the 32 shipped stage boards start lit by a volume, a volume read that way switches the fog
    /// off on 163 of the 173 volumes (the boards author fogEnabled = false), and the two boards rendered changed
    /// by 17,117 and 46,353 pixels at Start. "Start" is not this feature's to move. So a stage keeps whatever
    /// Start has in every field a volume does not model - a stage view never shows a default fog or shadow value
    /// the Start view does not - and changes only what the volumes change.</para>
    /// </summary>
    public static MapSunProperties WithVolumeFields(MapSunProperties? board, MapSunProperties lit)
    {
        ArgumentNullException.ThrowIfNull(lit);
        return board is null ? lit : board with
        {
            SunColor = lit.SunColor,
            SunDirection = lit.SunDirection,
            SkyLightColor = lit.SkyLightColor,
            SkyLightScale = lit.SkyLightScale,
            LightMapColorScale = lit.LightMapColorScale,
            HorizonColor = lit.HorizonColor,
            GroundColor = lit.GroundColor,
            FogColor = lit.FogColor,
            FogStartAndEnd = lit.FogStartAndEnd,
        };
    }

    private static bool CoLocated(IReadOnlyList<MapLightingVolume> volumes) =>
        volumes.Count >= 2 && volumes.All(v => v.Transform == volumes[0].Transform);

    private static MapLightingVolume? OnFor(IReadOnlyList<MapLightingVolume> volumes, int mask)
    {
        MapLightingVolume? active = null;
        foreach (var v in volumes)
        {
            if ((v.VisibilityFlags & mask) == 0) continue;
            if (active is not null) return null;   // two volumes on at once: which one wins is not known
            active = v;
        }
        return active;
    }

    private static MapSunProperties ReadVolumeLighting(BinTreeStruct s, MapSunProperties? global)
    {
        var b = global ?? new MapSunProperties();
        return new MapSunProperties
        {
            SunColor = Vec4(s, "sunColor") ?? b.SunColor,
            SunDirection = Vec3(s, "sunDirection") ?? b.SunDirection,
            SkyLightColor = Vec4(s, "skyLightColor") ?? b.SkyLightColor,
            SkyLightScale = F32(s, "skyLightScale") ?? b.SkyLightScale,
            LightMapColorScale = F32(s, "lightMapColorScale") ?? b.LightMapColorScale,
            HorizonColor = Vec4(s, "horizonColor") ?? b.HorizonColor,
            GroundColor = Vec4(s, "groundColor") ?? b.GroundColor,
            FogColor = Vec4(s, "fogColor") ?? b.FogColor,
            FogStartAndEnd = Vec2(s, "fogStartAndEnd") ?? b.FogStartAndEnd,
        };
    }

    private static BinTreeProperty? Field(IReadOnlyDictionary<uint, BinTreeProperty> props, string name)
    {
        if (props.TryGetValue(HashAlgorithms.Fnv1aRaw(name), out var p)) return p;
        return props.TryGetValue(HashAlgorithms.Fnv1a(name), out p) ? p : null;
    }

    private static Vector4? Vec4(BinTreeStruct s, string n) => Field(s.Properties, n) is BinTreeVector4 v ? v.Value : null;
    private static Vector3? Vec3(BinTreeStruct s, string n) => Field(s.Properties, n) is BinTreeVector3 v ? v.Value : null;
    private static Vector2? Vec2(BinTreeStruct s, string n) => Field(s.Properties, n) is BinTreeVector2 v ? v.Value : null;
    private static float? F32(BinTreeStruct s, string n) => Field(s.Properties, n) is BinTreeF32 v ? v.Value : null;
}

/// <summary>
/// A region of a map that overrides the global sun/atmosphere. 172 of them across 150 bins, each a real
/// placeable in <c>MapPlaceableContainer.items</c> with its own transform.
///
/// <para><b>Why this matters:</b> 145 of the 172 carry a <c>lightMapColorScale</c> (usually 2.0) that their
/// bin's global <c>MapSunProperties</c> does not have at all. ReyEngine therefore renders those regions at
/// 1.0 - roughly half as bright as authored. Map11 has no volumes; the affected maps are Map22/Map30/Map33.</para>
///
/// <para><b>Not applied to the viewport yet, on purpose.</b> Two things are unresolved and guessing at
/// either produces a wrong picture rather than a missing one:</para>
/// <list type="number">
///   <item>Whether the transform's basis vectors are HALF-extents or full extents. The arithmetic of one
///     sample (centre (2000, 2000) against a bounds max of (4000, 4000)) suggests half, but that is one
///     sample. Full extents would make every box twice its true size and activate the volume while the
///     camera is outside it.</item>
///   <item>Whether a volume blends at its boundary or switches hard. No field in the schema plausibly
///     encodes a blend width, and the two unresolved F32s are 0 on all three occurrences - consistent with
///     a falloff left at default, but that proves nothing. 22 bins carry two volumes, so a hard switch
///     would pop visibly as the camera crosses.</item>
/// </list>
/// </summary>
public sealed record MapLightingVolume(string Name, Matrix4x4 Transform, MapSunProperties Lighting, int VisibilityFlags)
{
    public Vector3 Position => Transform.Translation;
}

/// <summary>
/// A point light DEFINITION with no position - 788 of them across 139 bins.
///
/// <para>This class cannot be rendered, and that is a property of the shipped data rather than a gap in
/// ReyEngine. Its entire schema is five fields and none of them is a position; the placeable records that
/// would have carried the transform ship as NULL POINTERS (one measured bin is 205 of 205 null, and 85,335
/// of 151,457 placeable values game-wide - 56.3% - are stripped the same way). Nothing references the
/// definitions either: a scan of 144,726 ObjectLink and 179,468 Hash properties across every bin in
/// Maps/Shipping, DATA.wad.client and Global.wad.client found zero hits against the 401 distinct point-light
/// entry hashes.</para>
///
/// <para>So these are surfaced for inspection only, and any UI showing them must say why they cannot be
/// placed - otherwise it reads as a ReyEngine bug rather than a Riot build-stripping fact. Whether the
/// client uses them at all, or whether they are bake-time-only data, is NOT established: the absence of
/// any reference is strong evidence, but it is evidence of absence, not a traced code path.</para>
///
/// <para>Every field is nullable because absence is common and Riot omits defaults: 182 of 788 omit
/// <c>radius</c> and 10 omit <c>lightColor</c>. <c>Impact</c> is only ever 1 (328 of 788) and
/// <c>HdrScale</c> has 8 samples - neither meaning is established, so neither is interpreted.</para>
/// </summary>
public sealed record MapPointLightDefinition(
    uint EntryHash, Vector4? Color, float? Radius, int? Impact, bool? CastStaticShadows, float? HdrScale);
