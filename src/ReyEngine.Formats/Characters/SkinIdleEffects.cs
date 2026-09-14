using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Characters;

/// <summary>
/// M724: one <c>SkinCharacterDataProperties_CharacterIdleEffect</c> - a VFX the skin plays for as long as
/// the character exists, riding a named bone.
/// </summary>
/// <param name="EffectKey">The <c>effectKey</c> hash, resolved through the skin's own ResourceResolver
/// (<c>mResourceResolver</c> -> <c>resourceMap</c>) to a VfxSystemDefinitionData.</param>
/// <param name="BoneName">The joint the effect rides. Present on every one of the 1,855 records censused.</param>
/// <param name="TargetBoneName">Where a beam-shaped effect aims its far end; "" when the record authors
/// none. Real but uncommon - 192 of Thresh's 1,047 records and 12 of Yasuo's 95.</param>
/// <param name="Position">An offset in the joint's own frame, or null when unauthored. <b>Not observed in
/// the census</b> (0 of 1,855 records across Lux, Thresh, Viego, Jhin, Yasuo and Kayn), so this is read
/// defensively rather than from measured data - do not build behaviour on it without finding a real case.</param>
public sealed record SkinIdleEffect(uint EffectKey, string BoneName, string TargetBoneName = "", Vector3? Position = null);

/// <summary>
/// M724: reads a skin bin's <c>idleParticlesEffects</c> as a LIST, in order, with nothing collapsed.
///
/// <para><b>Why this is not <see cref="VfxSystemRoles.FromSkin"/>.</b> That reader answers a different
/// question - "what role does this effect key play?" - and to answer it keys a dictionary by
/// <c>effectKey</c> and keeps the first record per key. Idle effects do not survive that: <b>Lux's two idle
/// effects share one effect key (<c>Lux_I_weapon</c>) and differ only by bone</b>, BUFFBONE_CSTM_WEAPON_2
/// and BUFFBONE_CSTM_WEAPON_4, so keying by effect key drops one of her two wand glows. It is not an edge
/// case: a repeated key appears in 74 of Lux's 115 skins, 103 of Thresh's 129 and 42 of Viego's 42. The role
/// reader is right for roles and wrong for playback, so playback gets its own reader.</para>
///
/// <para>Never throws; an unreadable bin yields an empty list.</para>
/// </summary>
public static class SkinIdleEffects
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly uint SkinClass = H("SkinCharacterDataProperties");
    private static readonly uint FIdleEffects = H("idleParticlesEffects");
    private static readonly uint FEffectKey = H("effectKey");
    private static readonly uint FBoneName = H("boneName");
    private static readonly uint FTargetBoneName = H("targetBoneName");
    private static readonly uint FPosition = H("Position");

    /// <summary>
    /// Every idle effect the skin declares, in bin order.
    /// </summary>
    /// <param name="skinBin">The SKIN bin's bytes - <c>data/characters/&lt;champ&gt;/skins/skinN.bin</c>.
    /// Must be the skin being previewed: a champion's dependency closure contains other skins' bins, and
    /// reading idle effects out of those mounts another skin's effects on this one's bones.</param>
    /// <param name="objectPath">When given, only the skin object with this path hash is read. Use it when
    /// the bytes may hold more than one <c>SkinCharacterDataProperties</c>.</param>
    public static IReadOnlyList<SkinIdleEffect> Read(byte[] skinBin, uint objectPath = 0)
    {
        var result = new List<SkinIdleEffect>();
        BinTree tree;
        try { tree = SafeBinTree.Parse(skinBin); }
        catch { return result; }

        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != SkinClass) continue;
            if (objectPath != 0 && o.PathHash != objectPath) continue;
            if (o.Properties.GetValueOrDefault(FIdleEffects) is not BinTreeContainer idles) continue;

            foreach (var el in idles.Elements.OfType<BinTreeStruct>())
            {
                // An effectKey of 0 (or a wrong-typed one) is kept out: it can name no system, and the
                // caller would have nothing to mount. Everything else is preserved verbatim, duplicates
                // included - see the class comment for why that matters.
                if (el.Properties.GetValueOrDefault(FEffectKey) is not BinTreeHash key || key.Value == 0) continue;
                result.Add(new SkinIdleEffect(
                    key.Value,
                    (el.Properties.GetValueOrDefault(FBoneName) as BinTreeString)?.Value ?? "",
                    (el.Properties.GetValueOrDefault(FTargetBoneName) as BinTreeString)?.Value ?? "",
                    el.Properties.GetValueOrDefault(FPosition) switch
                    {
                        BinTreeVector3 v3 => v3.Value,
                        BinTreeVector4 v4 => new Vector3(v4.Value.X, v4.Value.Y, v4.Value.Z),
                        _ => null,
                    }));
            }
        }
        return result;
    }
}
