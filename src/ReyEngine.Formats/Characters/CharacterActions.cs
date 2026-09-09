using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Characters;

public enum CharacterActionKind
{
    Ability,
    Attack,
    Movement,
    Recall,
    Idle,
    Emote,
    Death,
}

/// <summary>
/// One thing a character does, and the clip that shows it.
/// </summary>
/// <param name="Label">"Q — Cleave", "Move", "Recall".</param>
/// <param name="Clip">The clip that plays it, or null when this character has none. Null is a real
/// answer and has to be shown as one: 35 of 174 champions are missing at least one Spell clip, and 48
/// have no Recall in their base graph. A row that vanished would read as "this champion has no Q".</param>
/// <param name="Variants">Other clips for the same action — Spell3_North/South/East/West, Attack2, Crit,
/// Run_Fast. They are the same action performed differently, and hiding them loses most of what a
/// character actually shows.</param>
public sealed record CharacterAction(
    string Label,
    CharacterActionKind Kind,
    string? SpellName,
    AnimClipInfo? Clip,
    IReadOnlyList<AnimClipInfo> Variants)
{
    public bool HasClip => Clip is not null;

    /// <summary>Every VFX this action spawns, across its clip and its variants.</summary>
    public IReadOnlyList<AnimParticleEvent> ParticleEvents =>
        All.SelectMany(c => c.ParticleEvents ?? Array.Empty<AnimParticleEvent>()).ToList();

    public IReadOnlyList<AnimSoundEvent> SoundEvents =>
        All.SelectMany(c => c.SoundEvents ?? Array.Empty<AnimSoundEvent>()).ToList();

    private IEnumerable<AnimClipInfo> All => Clip is null ? Variants : new[] { Clip }.Concat(Variants);
}

/// <summary>
/// M611: what a character does, in the order you would want to look at it.
///
/// <para>An animation list is 55 clip names in alphabetical order, a third of which are unnamed hashes.
/// That is a list of files. What a character actually does is four abilities, an attack, a walk, a
/// recall and a handful of emotes — and the mapping between the two is not obvious from either side.</para>
///
/// <para>Both halves come from shipped data. The abilities and their ORDER come from the champion
/// record's <c>spellNames</c>; the clip that plays each one comes from Riot's own <c>SpellN</c> naming in
/// the animation graph. Neither is inferred from the other.</para>
///
/// <para>Measured across the 174 shipped champions: 173 ship exactly four spell names; 139 ship all four
/// Spell clips, 34 ship some and 1 ships none; 169 have Run, 126 have Recall, 173 have a death clip. So
/// a missing clip is normal rather than exceptional, and is reported rather than hidden.</para>
///
/// <para>This is NOT a replacement for <see cref="Vfx.ChampionEventBuilder"/> (M116), and the two answer
/// different questions from different evidence. That one reads the VFX SYSTEM NAMES — a particle called
/// <c>..._q_...</c> is grouped under Q — which finds the effects but only for champions whose particles
/// are named that way, and has no notion of walking or recalling at all. This one reads the champion
/// record and the animation graph, so the four slots are always present, in Riot's order, under Riot's
/// names. The intended pairing is this list as the spine with those systems hung off each row.</para>
/// </summary>
public static class CharacterActions
{
    /// <summary>Build the action list. <paramref name="spellNames"/> may be empty — the four ability rows
    /// still appear, labelled by slot alone.</summary>
    public static IReadOnlyList<CharacterAction> Build(
        IReadOnlyList<string> spellNames, IReadOnlyList<AnimClipInfo> clips)
    {
        var actions = new List<CharacterAction>();

        // ---- the four abilities, in slot order --------------------------------------------------
        for (int i = 0; i < 4; i++)
        {
            string slot = "QWER"[i].ToString();
            string? spell = i < spellNames.Count && spellNames[i].Length > 0 ? spellNames[i] : null;
            string readable = spell is null ? "" : ChampionRecord.Readable(spell);
            string label = readable.Length > 0 ? $"{slot} — {readable}" : slot;

            // Riot numbers the clips from 1: Spell1 is the first ability. An exact name is the ability
            // itself; Spell3_North and friends are the same ability aimed differently.
            string prefix = "Spell" + (i + 1);
            var exact = Find(clips, n => n.Equals(prefix, StringComparison.OrdinalIgnoreCase));
            var related = clips.Where(c => !ReferenceEquals(c, exact) && IsVariantOf(c.Name, prefix)).ToList();

            // M663: SpellN is not the only convention. Aatrox's graph has no Spell1 or Spell2 anywhere in
            // his wad - his Q, which has three swings, is named Q1/Q2/Q3 - so both slots came up empty.
            // Tried second, so a champion that uses both keeps SpellN as its primary.
            if (exact is null && related.Count == 0)
                related = clips.Where(c => IsSlotLetterClip(SlotClipName(c, slot[0]), slot[0]))
                    // Ordered, because which one stands in below must not depend on the order the caller
                    // happened to gather the graphs in. Lowest cast number first (Q1 is the opener),
                    // then a bare cast over a transition. M674 also uses the filename behind unresolved
                    // graph keys: Aatrox's actual ground_q1/2/3 swings must beat Q1_INTO_Idle and friends.
                    .OrderBy(c => CastNumber(SlotClipName(c, slot[0])))
                    .ThenBy(c => SlotClipName(c, slot[0]).Contains('_') ? 1 : 0)
                    .ThenBy(c => c.AnmPath.Contains("_ult_", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            // With no plain SpellN, the first variant stands in - a champion whose Q only exists as
            // Spell1_ToIdle still has a Q worth playing.
            if (exact is null && related.Count > 0)
            {
                exact = related[0];
                related = related.Skip(1).ToList();
            }

            actions.Add(new CharacterAction(label, CharacterActionKind.Ability, spell, exact, related));
        }

        // ---- everything else ---------------------------------------------------------------------
        Add(actions, clips, "Attack", CharacterActionKind.Attack, "Attack1", "Attack");
        Add(actions, clips, "Critical Attack", CharacterActionKind.Attack, "Crit", "Crit");
        Add(actions, clips, "Move", CharacterActionKind.Movement, "Run", "Run");
        Add(actions, clips, "Recall", CharacterActionKind.Recall, "Recall", "Recall");
        Add(actions, clips, "Respawn", CharacterActionKind.Recall, "Respawn", "Respawn");
        Add(actions, clips, "Idle", CharacterActionKind.Idle, "Idle", "Idle");
        Add(actions, clips, "Dance", CharacterActionKind.Emote, "Dance", "Dance");
        Add(actions, clips, "Taunt", CharacterActionKind.Emote, "Taunt", "Taunt");
        Add(actions, clips, "Joke", CharacterActionKind.Emote, "Joke", "Joke");
        Add(actions, clips, "Laugh", CharacterActionKind.Emote, "Laugh", "Laugh");
        Add(actions, clips, "Death", CharacterActionKind.Death, "Death", "Death");

        return actions;
    }

    /// <summary>Clips that belong to no action above — a third of a champion's clips are transitions,
    /// stuns, channels and hash-only names, and they are still worth reaching.</summary>
    public static IReadOnlyList<AnimClipInfo> Unassigned(
        IReadOnlyList<CharacterAction> actions, IReadOnlyList<AnimClipInfo> clips)
    {
        var used = new HashSet<AnimClipInfo>(actions.SelectMany(a =>
            a.Clip is null ? a.Variants : new[] { a.Clip }.Concat(a.Variants)));
        return clips.Where(c => !used.Contains(c)).ToList();
    }

    private static void Add(List<CharacterAction> actions, IReadOnlyList<AnimClipInfo> clips,
        string label, CharacterActionKind kind, string exactName, string prefix)
    {
        var exact = Find(clips, n => n.Equals(exactName, StringComparison.OrdinalIgnoreCase));
        var related = clips.Where(c => !ReferenceEquals(c, exact) && IsVariantOf(c.Name, prefix)).ToList();

        if (exact is null && related.Count > 0)
        {
            exact = related[0];
            related = related.Skip(1).ToList();
        }

        // Only skip when the character genuinely has nothing: an empty Taunt row on a champion that
        // never taunts is noise, but an empty Q row on a champion that has a Q is information.
        if (exact is null && kind is CharacterActionKind.Emote or CharacterActionKind.Attack) return;
        actions.Add(new CharacterAction(label, kind, null, exact, related));
    }

    /// <summary>
    /// M663: Riot's OTHER ability naming — the slot letter, optionally a cast number, optionally a
    /// suffix: <c>Q</c>, <c>Q1</c>, <c>Q1_INTO_Idle</c>, <c>Q3_INTO_Run</c>.
    ///
    /// <para>The rule is deliberately tight. After the letter it accepts only digits, then either the end
    /// of the name or an underscore — because a loose prefix test on "R" would swallow <c>Recall</c>,
    /// <c>Run</c>, <c>Run_Base</c>, <c>Respawn</c> and <c>Recall_Winddown</c>, all of which Aatrox's own
    /// graph carries, and would put the recall animation under the ultimate for most of the roster.</para>
    /// </summary>
    public static bool IsSlotLetterClip(string clipName, char slot)
    {
        if (string.IsNullOrEmpty(clipName)) return false;
        if (char.ToUpperInvariant(clipName[0]) != char.ToUpperInvariant(slot)) return false;
        int i = 1;
        while (i < clipName.Length && char.IsAsciiDigit(clipName[i])) i++;
        return i == clipName.Length || clipName[i] == '_';
    }

    /// <summary>A hash-only graph key can still point at a named cast file. Only use whole
    /// filename tokens, and only for unresolved keys; SpellN graph names remain authoritative.</summary>
    public static string SlotClipName(AnimClipInfo clip, char slot)
    {
        if (!clip.Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return clip.Name;
        string file = System.IO.Path.GetFileNameWithoutExtension(clip.AnmPath.Replace('\\', '/'));
        var tokens = file.Split('_');
        for (int i = 0; i < tokens.Length; i++)
            if (IsSlotLetterClip(tokens[i], slot)) return string.Join("_", tokens.Skip(i));
        return clip.Name;
    }

    /// <summary>The digits after the slot letter (Q3 -> 3), or 0 when the name is just the letter.</summary>
    private static int CastNumber(string clipName)
    {
        int i = 1, n = 0;
        while (i < clipName.Length && char.IsAsciiDigit(clipName[i])) n = n * 10 + (clipName[i++] - '0');
        return n;
    }

    private static AnimClipInfo? Find(IReadOnlyList<AnimClipInfo> clips, Func<string, bool> match) =>
        clips.FirstOrDefault(c => match(c.Name));

    /// <summary>A variant is the prefix followed by a separator — Spell3_North, Run_Fast, Dance_Loop.
    /// The separator matters: without it "Spell1" would swallow "Spell11", and "Recall" would swallow
    /// nothing useful but "Run" would swallow "Runeterra".</summary>
    private static bool IsVariantOf(string clipName, string prefix)
    {
        if (clipName.Length <= prefix.Length) return false;
        if (!clipName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        char next = clipName[prefix.Length];
        return next is '_' or '-' or ' ' || char.IsDigit(next);
    }
}
