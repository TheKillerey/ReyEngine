using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Characters;

/// <summary>
/// M902: one <c>PersistentVfxData</c> of a <c>PersistentEffectConditionData</c> - an effect the game keeps running
/// while the owning condition holds, riding a bone.
/// </summary>
/// <param name="EffectKey">The <c>effectKey</c> hash, resolved through the skin's ResourceResolver like an idle effect's.</param>
/// <param name="BoneName">The joint it rides; "" when the record names none (Kai'Sa's Kaisa_E_active, Aatrox's camera veins).</param>
/// <param name="TargetBoneName">The far end of a beam-shaped effect, "" when none.</param>
/// <param name="AttachToCamera">The effect rides the camera, not the character (Aatrox's R veins). The character window has no
/// gameplay camera, so such a record is read and kept but not mounted.</param>
/// <param name="ShowToOwnerOnly">Read for completeness; a preview has one viewer, the owner.</param>
/// <param name="Scale">The record's <c>scale</c> (schema default 1). Read, not applied: no skin of the three checked authors one.</param>
/// <param name="Source">The whole struct, so every field this reader does not name (PlaySpeedModifier, SpecificTeam, ...) is still there.</param>
public sealed record PersistentVfx(uint EffectKey, string BoneName, string TargetBoneName, bool AttachToCamera,
    bool ShowToOwnerOnly, float Scale, BinTreeStruct Source);

/// <summary>
/// M902: one <c>PersistentEffectConditionData</c> of <c>SkinCharacterDataProperties.PersistentEffectConditions</c>:
/// a condition tree, and what is on while it holds - effects, and submeshes to show or hide.
/// </summary>
/// <param name="OwnerCondition">The condition tree (an <c>IValueProcessor</c>: HasBuff, IsAnimationPlaying, Not, AllTrue, ...);
/// null when the record authors none.</param>
/// <param name="HasSourceCondition">The record also authors a <c>SourceCondition</c> (a condition on whoever applied a buff).
/// A preview has no second unit, so this is read, kept in <paramref name="Source"/>, and not evaluated.</param>
public sealed record PersistentEffectCondition(int Index, BinTreeStruct? OwnerCondition, bool HasSourceCondition,
    IReadOnlyList<PersistentVfx> Vfxs, IReadOnlyList<uint> SubmeshesToShow, IReadOnlyList<uint> SubmeshesToHide,
    BinTreeStruct Source);

/// <summary>
/// M902: reads a skin bin's <c>PersistentEffectConditions</c>, in order, nothing collapsed. READ ONLY: the structs are kept as
/// parsed and nothing here writes a bin.
///
/// <para>Why it exists: <c>idleParticlesEffects</c> (<see cref="SkinIdleEffects"/>) is the always-on list, and this is the
/// other one - effects that run only while a condition holds. Ahri skin 97's orb and tail fire, Kai'Sa skin 83's engine glow
/// and Aatrox skin 33's banner turbines live ONLY here. 1,000+ lines of each skin's bin were read by nothing but the role
/// labeller (<see cref="VfxSystemRoles"/>).</para>
///
/// <para>Never throws; an unreadable bin yields an empty list.</para>
/// </summary>
public static class SkinPersistentEffects
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly uint SkinClass = H("SkinCharacterDataProperties");
    private static readonly uint FConditions = H("PersistentEffectConditions");
    private static readonly uint FOwner = H("OwnerCondition");
    private static readonly uint FSource = H("SourceCondition");
    private static readonly uint FVfxs = H("PersistentVfxs");
    private static readonly uint FShow = H("SubmeshesToShow");
    private static readonly uint FHide = H("SubmeshesToHide");
    private static readonly uint FEffectKey = H("effectKey");
    private static readonly uint FBone = H("boneName");
    private static readonly uint FTarget = H("targetBoneName");
    private static readonly uint FCamera = H("AttachToCamera");
    private static readonly uint FOwnerOnly = H("ShowToOwnerOnly");
    private static readonly uint FScale = H("scale");

    public static IReadOnlyList<PersistentEffectCondition> Read(byte[] skinBin, uint objectPath = 0)
    {
        var result = new List<PersistentEffectCondition>();
        BinTree tree;
        try { tree = SafeBinTree.Parse(skinBin); }
        catch { return result; }

        foreach (var o in tree.Objects.Values)
        {
            if (o.ClassHash != SkinClass) continue;
            if (objectPath != 0 && o.PathHash != objectPath) continue;
            if (o.Properties.GetValueOrDefault(FConditions) is not BinTreeContainer list) continue;

            foreach (var el in list.Elements.OfType<BinTreeStruct>())
            {
                var vfxs = new List<PersistentVfx>();
                if (el.Properties.GetValueOrDefault(FVfxs) is BinTreeContainer vl)
                    foreach (var v in vl.Elements.OfType<BinTreeStruct>())
                    {
                        // an effectKey of 0 names no system; everything else is kept, duplicates included
                        if (v.Properties.GetValueOrDefault(FEffectKey) is not BinTreeHash key || key.Value == 0) continue;
                        vfxs.Add(new PersistentVfx(key.Value,
                            (v.Properties.GetValueOrDefault(FBone) as BinTreeString)?.Value ?? "",
                            (v.Properties.GetValueOrDefault(FTarget) as BinTreeString)?.Value ?? "",
                            Bool(v.Properties.GetValueOrDefault(FCamera)),
                            Bool(v.Properties.GetValueOrDefault(FOwnerOnly)),
                            v.Properties.GetValueOrDefault(FScale) is BinTreeF32 s ? s.Value : 1f,
                            v));
                    }
                result.Add(new PersistentEffectCondition(result.Count,
                    Tree(el.Properties.GetValueOrDefault(FOwner)),
                    Tree(el.Properties.GetValueOrDefault(FSource)) is not null,
                    vfxs, Hashes(el.Properties.GetValueOrDefault(FShow)), Hashes(el.Properties.GetValueOrDefault(FHide)), el));
            }
        }
        return result;
    }

    /// <summary>A pointer field's struct, or null when it is absent or the null pointer (class 0).</summary>
    private static BinTreeStruct? Tree(BinTreeProperty? p) => p is BinTreeStruct { ClassHash: not 0 } s ? s : null;

    private static bool Bool(BinTreeProperty? p) => p switch { BinTreeBool b => b.Value, BinTreeBitBool bb => bb.Value, _ => false };

    private static IReadOnlyList<uint> Hashes(BinTreeProperty? p) =>
        p is BinTreeContainer c ? c.Elements.OfType<BinTreeHash>().Select(h => h.Value).ToList() : Array.Empty<uint>();
}

/// <summary>
/// M902: one switch the persistent conditions of a skin ask about.
/// </summary>
/// <param name="Condition">In the M647 vocabulary (<see cref="MaterialDriverCondition"/>): the key is stable, e.g. <c>HasBuff:AhriR</c>.</param>
/// <param name="DefaultOn">What the preview starts it as. <b>An inference</b> - see <see cref="PersistentConditions.DefaultOn"/>.</param>
public readonly record struct PersistentGate(MaterialDriverCondition Condition, bool DefaultOn)
{
    public string Key => Condition.Key;
}

/// <summary>What a persistent condition tree is evaluated against.</summary>
public sealed class PersistentContext
{
    /// <summary>The graph names (hashes) of the clip playing now: <see cref="AnimationClipGraph.HashOf"/> of each clip that plays the selected file.</summary>
    public IReadOnlySet<uint> PlayingClips { get; init; } = new HashSet<uint>();

    /// <summary>Whether a submesh named by one of these hashes is visible now (any of them).</summary>
    public Func<IReadOnlyList<uint>, bool> AnySubmeshVisible { get; init; } = _ => false;

    /// <summary>Whether a switch is on, by <see cref="MaterialDriverCondition.Key"/>.</summary>
    public Func<string, bool> IsOn { get; init; } = _ => false;

    /// <summary>The previewed unit is a champion: a <c>UnitTags</c> test naming "champion" holds.</summary>
    public bool IsChampion { get; init; } = true;

    public Func<uint, string?> Resolve { get; init; } = _ => null;

    /// <summary>Called with a description for each condition class this evaluator does not know. The evaluator reports every
    /// occurrence; the caller de-duplicates ("logged once").</summary>
    public Action<string>? OnUnknown { get; init; }
}

/// <summary>
/// M902: the evaluator for <see cref="PersistentEffectCondition.OwnerCondition"/> trees.
///
/// <para><b>Vocabulary.</b> A leaf that asks "does he have this buff / gear / is he dead / moving..." is a switch keyed exactly
/// like the M647 material-driver conditions (<see cref="MaterialDrivers.ConditionOf"/>), so one name means one question across
/// the Material card and this one. Two leaves are NOT switches because the preview knows the answer: <c>IsAnimationPlaying</c>
/// reads the clip actually playing, and <c>SubmeshVisibilityBoolDriver</c> reads the submeshes actually shown.</para>
///
/// <para><b>Unknown conditions are OFF.</b> A condition class this evaluator cannot name makes the WHOLE entry off (not just
/// the leaf: a <c>Not</c> around an unknown would otherwise turn it on) and is reported through
/// <see cref="PersistentContext.OnUnknown"/>.</para>
/// </summary>
public static class PersistentConditions
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    private static readonly uint CNot = H("NotMaterialDriver");
    private static readonly uint CDelayed = H("DelayedBoolMaterialDriver");
    private static readonly uint CAll = H("AllTrueMaterialDriver");
    private static readonly uint COne = H("OneTrueMaterialDriver");
    private static readonly uint CAnim = H("IsAnimationPlayingDynamicMaterialBoolDriver");
    private static readonly uint CSubmesh = H("SubmeshVisibilityBoolDriver");
    private static readonly uint FDriver = H("mDriver");
    private static readonly uint FBoolDriver = H("mBoolDriver");
    private static readonly uint FDrivers = H("mDrivers");
    private static readonly uint FAnimNames = H("mAnimationNames");
    private static readonly uint FSubmeshes = H("Submeshes");
    private static readonly uint FUnitTags = H("UnitTags");
    private static readonly uint FTagList = H("mObjectTagList");
    private static readonly uint FGearIndex = H("mGearIndex");
    private static readonly uint TagChampion = H("champion");

    /// <summary>
    /// Which switches start on. <b>INFERENCE, not game data.</b> The reference viewer shows Ahri skin 97's orb at idle although
    /// the orb is gated by <c>HasBuff AhriIdleOrbVFXBuff</c>: that buff is a cosmetic the game gives an idling Ahri. Turning every
    /// buff on would be wrong - Kai'Sa's engine glow needs <c>NOT KaisaRShield</c> and Evelynn's eyes are her stealth ring - so
    /// only a buff whose own name says it is an idle cosmetic (contains "Idle") starts on. Gear, level and every other
    /// condition start at the base state (off).
    /// </summary>
    public static bool DefaultOn(MaterialDriverCondition c) =>
        c.Kind == "HasBuff" && c.Label.Contains("Idle", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every switch the entries ask about, deduplicated, in first-seen order.</summary>
    public static IReadOnlyList<PersistentGate> GatesOf(IEnumerable<PersistentEffectCondition> entries, Func<uint, string?> resolve)
    {
        var seen = new Dictionary<string, PersistentGate>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            if (e.OwnerCondition is { } tree) Collect(tree, resolve, seen);
        return seen.Values.ToList();
    }

    private static void Collect(BinTreeStruct d, Func<uint, string?> resolve, Dictionary<string, PersistentGate> into)
    {
        if (Leaf(d, resolve) is { Kind: LeafKind.Switch } leaf) { into.TryAdd(leaf.Condition.Key, new PersistentGate(leaf.Condition, DefaultOn(leaf.Condition))); return; }
        foreach (var p in d.Properties.Values)
            switch (p)
            {
                case BinTreeStruct s when s.ClassHash != 0: Collect(s, resolve, into); break;
                case BinTreeContainer c:
                    foreach (var el in c.Elements) if (el is BinTreeStruct es && es.ClassHash != 0) Collect(es, resolve, into);
                    break;
            }
    }

    /// <summary>Whether an entry is on. An entry without an owner condition is always on; one with only a SourceCondition
    /// (not evaluable here) is off.</summary>
    public static bool IsActive(PersistentEffectCondition entry, PersistentContext ctx)
    {
        if (entry.OwnerCondition is null) return !entry.HasSourceCondition;
        return Evaluate(entry.OwnerCondition, ctx) ?? false;
    }

    /// <summary>The tree's value; null when it contains a condition this evaluator does not know.</summary>
    public static bool? Evaluate(BinTreeStruct d, PersistentContext ctx)
    {
        uint cls = d.ClassHash;
        if (cls == CNot)
            return Child(d, FDriver) is { } inner && Evaluate(inner, ctx) is { } v ? !v : null;
        if (cls == CDelayed)   // a delay changes WHEN it flips, not what it settles on
            return Child(d, FBoolDriver) is { } delayed ? Evaluate(delayed, ctx) : null;
        if (cls == CAll || cls == COne)
        {
            if (d.Properties.GetValueOrDefault(FDrivers) is not BinTreeContainer c) return null;
            bool all = cls == CAll, acc = all;
            foreach (var e in c.Elements)
            {
                if (e is not BinTreeStruct s || Evaluate(s, ctx) is not { } x) return null;
                acc = all ? acc && x : acc || x;
            }
            return acc;
        }

        var leaf = Leaf(d, ctx.Resolve);
        switch (leaf.Kind)
        {
            case LeafKind.Switch: return ctx.IsOn(leaf.Condition.Key);
            case LeafKind.Animation:
                return d.Properties.GetValueOrDefault(FAnimNames) is BinTreeContainer names
                       && names.Elements.OfType<BinTreeHash>().Any(h => ctx.PlayingClips.Contains(h.Value));
            case LeafKind.Submesh:
                return d.Properties.GetValueOrDefault(FSubmeshes) is BinTreeContainer subs
                       && ctx.AnySubmeshVisible(subs.Elements.OfType<BinTreeHash>().Select(h => h.Value).ToList());
            case LeafKind.UnitTags:
                // 0x18278f59 { UnitTags: ObjectTags { mObjectTagList: [champion, ...] } } - read as "the unit has one of these
                // tags". INFERENCE: the class name is not in any hash table; the shape is the evidence.
                return ((d.Properties.GetValueOrDefault(FUnitTags) as BinTreeStruct)?.Properties.GetValueOrDefault(FTagList)
                        is BinTreeContainer tags) && ctx.IsChampion && tags.Elements.OfType<BinTreeHash>().Any(h => h.Value == TagChampion);
        }
        ctx.OnUnknown?.Invoke(ctx.Resolve(cls) ?? $"0x{cls:x8}");
        return null;
    }

    private static BinTreeStruct? Child(BinTreeStruct d, uint field) =>
        d.Properties.GetValueOrDefault(field) is BinTreeStruct { ClassHash: not 0 } s ? s : null;

    private enum LeafKind { None, Switch, Animation, Submesh, UnitTags }
    private readonly record struct LeafInfo(LeafKind Kind, MaterialDriverCondition Condition);

    private static LeafInfo Leaf(BinTreeStruct d, Func<uint, string?> resolve)
    {
        if (d.ClassHash == CAnim) return new(LeafKind.Animation, default);
        if (d.ClassHash == CSubmesh) return new(LeafKind.Submesh, default);
        if (d.ClassHash is var c && (c == CNot || c == CDelayed || c == CAll || c == COne)) return new(LeafKind.None, default);
        if (d.Properties.ContainsKey(FUnitTags)) return new(LeafKind.UnitTags, default);
        if (MaterialDrivers.ConditionOf(d, resolve) is { } cond)
        {
            if (cond.Kind == "HasGear")
            {
                int index = d.Properties.GetValueOrDefault(FGearIndex) is BinTreeU8 g ? g.Value : 0;
                cond = new MaterialDriverCondition($"HasGear:{index}", "HasGear", $"Gear {index}");
            }
            return new(LeafKind.Switch, cond);
        }
        return new(LeafKind.None, default);
    }
}
