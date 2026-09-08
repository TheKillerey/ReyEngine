namespace ReyEngine.Formats.Characters;

/// <summary>
/// M663: which bone a caster-side spell effect should ride when nothing in Riot's files says.
///
/// <para>An animation clip that authors a <c>ParticleEventData</c> names its own bone, and that name is
/// always preferred — this type is only consulted for the composite the editor assembles from VFX
/// <em>names</em> when the clip authors no event at all. That is not a rare corner: Blitzcrank's entire
/// base animation graph holds four ParticleEventData, none of them on a spell clip, because his abilities
/// are spawned by the compiled spell script, which ships in no bin. His Q therefore had no bone, drew in
/// world space, and stayed behind while he moved.</para>
///
/// <para><b>Measured</b> over all 174 champion wads (<c>bonecensus</c> probe): 115 champions author at
/// least one caster-side spell particle event, 540 events in total, 507 of which name a bone this reader
/// resolves against the skeleton. Those 507 spread over <b>113 distinct bones</b> — there is no bone that
/// is simply correct. But the head of the distribution is Riot's own standardised attachment locators:</para>
///
/// <code>
///   C_Buffbone_GLB_Layout_Loc      90   17.8%
///   BUFFBONE_GLB_GROUND_LOC        54   10.7%
///   R_Hand                         21    4.1%
///   C_Buffbone_Glb_Center_Loc      20    3.9%
///   R_Buffbone_Glb_Hand_Loc        19    3.7%
///   l_hand / l_buffbone_glb_hand_loc / root   18 each
/// </code>
///
/// <para>The <c>Buffbone_GLB_*</c> family together takes about 45%. The two at the top sit at the
/// character's layout origin and on the ground under it, so an effect bound to either rides the model and
/// keeps the model's facing — which is the behaviour that was missing — without claiming to know that a
/// particular spell comes off a particular hand. That is the default this picks, and it is a
/// <b>default</b>: the preview exposes every joint so the real bone can be chosen. Blitzcrank's fists come
/// off <c>R_Hand</c>/<c>l_hand</c>, and no file in the game will ever tell us so.</para>
/// </summary>
public static class CasterBone
{
    /// <summary>Riot's standard locators, best first. Matched case-insensitively against joint names.</summary>
    public static readonly IReadOnlyList<string> Preferred = new[]
    {
        "C_Buffbone_GLB_Layout_Loc",
        "BUFFBONE_GLB_GROUND_LOC",
        "C_Buffbone_Glb_Center_Loc",
        "C_BUFFBONE_GLB_CHEST_LOC",
        "Buffbone_Cstm_Ground",
        "Root",
    };

    /// <summary>
    /// The bone a name-assembled caster effect should ride, or null when the skeleton offers nothing —
    /// in which case the caller leaves the effect in world space, exactly as before.
    /// </summary>
    /// <param name="jointNames">Every joint in the character's skeleton, in file order.</param>
    public static string? Choose(IEnumerable<string>? jointNames)
    {
        if (jointNames is null) return null;
        var joints = jointNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (joints.Count == 0) return null;

        foreach (string want in Preferred)
        {
            var hit = joints.FirstOrDefault(j => j.Equals(want, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;   // the skeleton's own spelling, not ours
        }

        // No standard locator. Any buffbone locator beats an arbitrary joint - it is at least a point Riot
        // put there to hang things on, rather than an elbow.
        var buff = joints.FirstOrDefault(j => j.Contains("buffbone", StringComparison.OrdinalIgnoreCase));
        if (buff is not null) return buff;

        // 149 of 174 skeletons call it "Root"; the 25 that do not are C_Root, Root_Upper, root_b, Pelvis,
        // Scythe... - the first joint is the closest thing to a root those skeletons have.
        return joints[0];
    }
}
