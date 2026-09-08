using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M663: three faults reported together, all in how a spell reaches its animation and its bone.
///
/// <list type="number">
/// <item>Blitzcrank's base skin played <c>blitzcrank_skin20_spell2.anm</c> for W. The host keyed its clip
/// dictionary by <c>.anm</c> FILE name, and Riot points several clips at one file — the base graph names
/// <c>blitzcrank_spell1.anm</c> from Spell1, Spell2 and Spell2_BASE — so the base graph's own Spell2 was
/// dropped and a clip called Spell2 from another skin's graph answered instead.</item>
/// <item>Aatrox had no Q and no W. His graph contains no <c>SpellN</c> clip for those slots at all; his Q
/// is <c>Q1</c>/<c>Q2</c>/<c>Q3</c>, a second naming convention the matcher did not know.</item>
/// <item>His Q effect stood in world space, because a composite assembled from VFX names carries no bone
/// — see <see cref="CasterBone"/>.</item>
/// </list>
/// </summary>
public sealed class SpellClipBindingTests
{
    private static AnimClipInfo Clip(string name, string anm) =>
        new(name, anm, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<uint>(), Array.Empty<uint>());

    private static readonly string[] Qwer = { "RocketGrab", "Overdrive", "PowerFist", "StaticField" };

    private static AnimClipInfo? Slot(IReadOnlyList<CharacterAction> actions, char slot) =>
        actions.First(a => a.Kind == CharacterActionKind.Ability && a.Label.StartsWith(slot)).Clip;

    // ---- fix 1: several clips, one .anm file -------------------------------------------------------

    /// <summary>
    /// The shape that caused it, in miniature: the base graph's Spell1 and Spell2 both play
    /// blitzcrank_spell1.anm. Keyed by file, the second is lost; keyed by name, both survive and W finds
    /// the clip its own skin declares.
    /// </summary>
    [Fact]
    public void TwoClipsSharingOneAnmFileBothSurviveWhenKeyedByName()
    {
        var baseGraph = new[]
        {
            Clip("Spell1", "blitzcrank_spell1.anm"),
            Clip("Spell2", "blitzcrank_spell1.anm"),   // the SAME file - this is Riot's own data
        };
        var otherSkin = new[] { Clip("Spell2", "blitzcrank_skin20_spell2.anm") };

        // what the host used to build: first-wins by .anm FILE name
        var byFile = new Dictionary<string, AnimClipInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in baseGraph.Concat(otherSkin))
            if (!byFile.ContainsKey(c.AnmPath)) byFile[c.AnmPath] = c;

        // what it builds now: first-wins by CLIP name, this skin's graph inserted first
        var byName = new Dictionary<string, AnimClipInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in baseGraph.Concat(otherSkin)) byName.TryAdd(c.Name, c);

        Assert.Equal(2, byFile.Count);    // Spell1, Spell2_from_skin20 - the base Spell2 is gone
        Assert.DoesNotContain(byFile.Values, c => c.Name == "Spell2" && c.AnmPath.Contains("spell1"));

        Assert.Equal(2, byName.Count);
        Assert.Equal("blitzcrank_spell1.anm", Slot(CharacterActions.Build(Qwer, byName.Values.ToList()), 'W')!.AnmPath);
        Assert.Equal("blitzcrank_skin20_spell2.anm", Slot(CharacterActions.Build(Qwer, byFile.Values.ToList()), 'W')!.AnmPath);
    }

    // ---- fix 2: the slot-letter naming convention --------------------------------------------------

    [Theory]
    [InlineData("Q", 'Q', true)]
    [InlineData("Q1", 'Q', true)]
    [InlineData("Q3", 'Q', true)]
    [InlineData("Q1_INTO_Idle", 'Q', true)]
    [InlineData("q2_into_run", 'Q', true)]      // Riot's casing is not consistent
    [InlineData("W1", 'W', true)]
    [InlineData("Q1", 'W', false)]              // wrong slot
    // The names that make a loose prefix match unusable. Every one of these is in a real base graph,
    // and under "starts with R" they would all have become the ultimate.
    [InlineData("Recall", 'R', false)]
    [InlineData("Recall_Winddown", 'R', false)]
    [InlineData("Run", 'R', false)]
    [InlineData("Run_Base", 'R', false)]
    [InlineData("Respawn", 'R', false)]
    [InlineData("Crit", 'Q', false)]
    [InlineData("", 'Q', false)]
    public void OnlyTheLetterThenDigitsThenABoundaryIsASlotClip(string clip, char slot, bool expected) =>
        Assert.Equal(expected, CharacterActions.IsSlotLetterClip(clip, slot));

    /// <summary>Aatrox: no SpellN for Q at all, so the letter convention has to carry the slot.</summary>
    [Fact]
    public void ASlotWithNoSpellNFallsBackToTheLetterConvention()
    {
        var clips = new[]
        {
            Clip("Q1_INTO_Run", "aatrox_ground_q1_into_run.anm"),
            Clip("Q1_INTO_Idle", "aatrox_ground_q1_into_idle.anm"),
            Clip("Q3_INTO_Run", "aatrox_ground_q3_into_run.anm"),
            Clip("Q2_INTO_Idle", "aatrox_ground_q2_into_idle.anm"),
            Clip("Spell3", "aatrox_spell3.anm"),
            Clip("Recall", "aatrox_recall.anm"),
            Clip("Run", "aatrox_run.anm"),
        };
        var actions = CharacterActions.Build(Qwer, clips);

        // Q resolves, and to the LOWEST cast number - the order the clips arrived in must not decide it
        Assert.Equal("Q1_INTO_Idle", Slot(actions, 'Q')!.Name);
        var q = actions.First(a => a.Label.StartsWith('Q'));
        Assert.Equal(3, q.Variants.Count);

        // E still comes from SpellN, and R stays empty rather than swallowing Recall/Run
        Assert.Equal("Spell3", Slot(actions, 'E')!.Name);
        Assert.Null(Slot(actions, 'R'));
    }

    /// <summary>SpellN stays primary for a champion that happens to carry both conventions.</summary>
    [Fact]
    public void SpellNWinsOverTheLetterConventionWhenBothExist()
    {
        var clips = new[] { Clip("Q1", "q1.anm"), Clip("Spell1", "spell1.anm") };
        Assert.Equal("Spell1", Slot(CharacterActions.Build(Qwer, clips), 'Q')!.Name);
    }

    // ---- fix 3: the bone a name-assembled caster effect rides ---------------------------------------

    [Fact]
    public void TheCasterBoneDefaultsToRiotsOwnAttachmentLocator()
    {
        // Blitzcrank's skeleton, in the order the file lists it - the locator is not first
        var joints = new[] { "Root", "Pelvis", "Spine1", "R_Hand", "C_BUFFBONE_GLB_LAYOUT_LOC", "L_Hand" };
        Assert.Equal("C_BUFFBONE_GLB_LAYOUT_LOC", CasterBone.Choose(joints));
    }

    [Fact]
    public void TheSkeletonsOwnSpellingIsKept()
    {
        // 90 events bind to this locator across the roster and the casing varies by champion; the picker
        // must hand back the name the skeleton uses, because the viewport looks the bone up by it.
        Assert.Equal("c_buffbone_glb_layout_loc", CasterBone.Choose(new[] { "Root", "c_buffbone_glb_layout_loc" }));
    }

    [Fact]
    public void TheLocatorsAreTriedInTheOrderTheCensusRanks()
    {
        // Layout_Loc (90 events) over GROUND_LOC (54) over Center_Loc (20), whichever the skeleton has
        Assert.Equal("BUFFBONE_GLB_GROUND_LOC",
            CasterBone.Choose(new[] { "Root", "BUFFBONE_GLB_GROUND_LOC", "C_Buffbone_Glb_Center_Loc" }));
        Assert.Equal("C_Buffbone_GLB_Layout_Loc",
            CasterBone.Choose(new[] { "BUFFBONE_GLB_GROUND_LOC", "C_Buffbone_GLB_Layout_Loc" }));
    }

    [Fact]
    public void AnyBuffboneBeatsAnArbitraryJointAndTheRootIsTheLastResort()
    {
        Assert.Equal("Buffbone_Cstm_Weapon_1",
            CasterBone.Choose(new[] { "C_Root", "Spine1", "Buffbone_Cstm_Weapon_1", "Elbow" }));
        // no locator and no joint called Root: the first joint is the closest thing the skeleton has
        Assert.Equal("C_Root", CasterBone.Choose(new[] { "C_Root", "Spine1", "Elbow" }));
    }

    [Fact]
    public void NoSkeletonMeansNoBoneRatherThanAGuess()
    {
        Assert.Null(CasterBone.Choose(null));
        Assert.Null(CasterBone.Choose(Array.Empty<string>()));
        Assert.Null(CasterBone.Choose(new[] { "", "   " }));
    }
}
