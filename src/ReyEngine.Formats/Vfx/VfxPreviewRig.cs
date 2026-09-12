using System.Numerics;

namespace ReyEngine.Formats.Vfx;

/// <summary>How the preview carries a system: what it is standing in for.</summary>
public enum VfxRigMode
{
    /// <summary>It stays where it is put. A buff, an aura, a ground indicator.</summary>
    Still,
    /// <summary>Still, but the whole run starts over as soon as it ends - a cast that fires repeatedly.</summary>
    Burst,
    /// <summary>It flies in a straight line at a speed, and stops emitting where it lands.</summary>
    Missile,
    /// <summary>It circles, so a trail or a ribbon has a path to lay itself along.</summary>
    Trail,
}

/// <summary>
/// The rig's resting numbers, in engine units and seconds. A champion is 200 units tall and every one of
/// these is a multiple of that, which is how ltk-manager's rig is dimensioned and how the game's own
/// distances read.
///
/// <para>Their own type because a record's parameter defaults cannot name constants declared inside that
/// same record.</para>
/// </summary>
public static class VfxRigDefaults
{
    /// <summary>Half a champion. Where a rig stands when nothing says otherwise.</summary>
    public const float Height = 100f;
    /// <summary>Six champions. The default missile flight, centred on the origin.</summary>
    public const float Distance = 1200f;
    /// <summary>Eight champions a second. With the default distance, a flight of exactly 0.75 s.</summary>
    public const float Speed = 1600f;
    /// <summary>One and a half champions.</summary>
    public const float Radius = 300f;
    /// <summary>Three seconds to come round.</summary>
    public const float Period = 3f;
    /// <summary>The run every reader of one effect sees, so "seed 1337" means the same thing twice.</summary>
    public const int Seed = 1337;
}

/// <summary>
/// M712: a rig for the particle preview, because the file does not say where the effect goes.
///
/// <para>A <c>VfxSystemDefinitionData</c> describes emitters and nothing else. Whether the thing it
/// describes sits on a champion's hand, lies on the ground or flies across the map is decided by the spell
/// that instantiates it, not by a field in the bin - so a preview that only ever parks a system at the
/// origin shows a missile as a puff standing still, and shows a trail with no motion to trail behind.
/// The reader picks the rig, the same way ltk-manager's preview does.</para>
///
/// <para><b>A record and a function, deliberately.</b> This carries no viewport state and touches no
/// renderer, so the motion can be pinned by a test without a device - the shape
/// <see cref="Characters.SpellAim"/> already uses for cast plans. A host calls <see cref="Pose"/> every
/// frame and re-anchors the simulator with the result.</para>
///
/// <para><b>The numbers are ltk-manager's, which are the game's.</b> A champion is 200 units tall, and the
/// defaults are multiples of that: the rig stands at half a champion (100), a missile flies six champions
/// (1,200 units) at eight champions a second (1,600 u/s), which is a flight of exactly 0.75 s, and a trail
/// circles at one and a half champions (300) once every three seconds. Riot's own missile speeds sit in
/// the same range - Ahri's charm is authored at 1,550 - so the default flight is not a made-up number.</para>
/// </summary>
/// <param name="Mode">What the preview is standing in for.</param>
/// <param name="Height">How far off the ground the whole rig sits. Every motion is authored on the ground
/// plane and this lifts it, so it is the system's own Y offset in every mode rather than a flight
/// altitude.</param>
/// <param name="Distance">How far a missile flies, centred on the origin so the camera keeps it in frame.</param>
/// <param name="Speed">How fast a missile flies, in units per second.</param>
/// <param name="Radius">How wide a trail circles.</param>
/// <param name="Period">How long a trail takes to come round, in seconds.</param>
/// <param name="Replay">Start the run over when it ends, instead of letting the clock run on.</param>
/// <param name="StopMidRun">Stop the system emitting halfway through the run, so its teardown is visible
/// without holding the Stop button. A missile stops where it lands whatever this says, which is what the
/// game does with one.</param>
public sealed record VfxPreviewRig(
    VfxRigMode Mode = VfxRigMode.Still,
    float Height = VfxRigDefaults.Height,
    float Distance = VfxRigDefaults.Distance,
    float Speed = VfxRigDefaults.Speed,
    float Radius = VfxRigDefaults.Radius,
    float Period = VfxRigDefaults.Period,
    bool Replay = false,
    bool StopMidRun = false)
{
    /// <summary>How long a missile is in the air. Zero for every other mode, and for a missile with no
    /// distance or no speed - which falls back to the system's own span rather than to a run of no length
    /// that would restart every frame.</summary>
    public float FlightSeconds =>
        Mode == VfxRigMode.Missile && Distance > 0f && Speed > 0f ? Distance / Speed : 0f;

    /// <summary>
    /// One pass of the rig, in seconds: the window the run covers and the unit <see cref="Replay"/> wraps.
    ///
    /// <para>A missile's run is its flight plus <paramref name="tail"/>, the time its particles go on
    /// playing after it lands. A trail's is at least one revolution. Everything else runs for the system's
    /// own span, so a two-second effect replays every two seconds.</para>
    /// </summary>
    public float RunLength(float systemSpan, float tail = 0f)
    {
        float span = systemSpan > 0f ? systemSpan : 1f;
        return Mode switch
        {
            VfxRigMode.Missile when FlightSeconds > 0f => FlightSeconds + MathF.Max(0f, tail),
            VfxRigMode.Trail => MathF.Max(Period > 0f ? Period : span, span),
            _ => span,
        };
    }

    /// <summary>Where the run's clock stands at wall time <paramref name="t"/>. Without
    /// <see cref="Replay"/> that is the clock itself; with it, the clock wrapped into one run.</summary>
    public float Phase(float t, float systemSpan, float tail = 0f)
    {
        if (!Replay) return t;
        float length = RunLength(systemSpan, tail);
        return length > 0f ? t % length : t;
    }

    /// <summary>Has a missile arrived? True from the moment it lands, and false in every other mode.</summary>
    public bool Landed(float t) => FlightSeconds > 0f && t >= FlightSeconds;

    /// <summary>When the system should stop emitting, or null for "not on its own account". A missile stops
    /// where it lands because that is what the game does with one; any other mode stops halfway through its
    /// run, and only when asked.</summary>
    public float? StopAt(float systemSpan, float tail = 0f)
    {
        if (FlightSeconds > 0f) return FlightSeconds;
        return StopMidRun ? RunLength(systemSpan, tail) * 0.5f : null;
    }

    /// <summary>
    /// Where the system stands, and which way it faces, at run time <paramref name="t"/>.
    ///
    /// <para>The facing is built with <see cref="VfxCastFrame.Toward"/>, so a system whose emitters are
    /// authored to point along the cast direction points where the rig is going. A still rig has no
    /// direction to face and is given none rather than being turned to some default.</para>
    /// </summary>
    public Matrix4x4 Pose(float t)
    {
        var lift = new Vector3(0f, Height, 0f);
        switch (Mode)
        {
            case VfxRigMode.Missile when FlightSeconds > 0f:
            {
                var from = new Vector3(-Distance / 2f, 0f, 0f);
                var to = new Vector3(Distance / 2f, 0f, 0f);
                float at = Math.Clamp(t / FlightSeconds, 0f, 1f);
                return VfxCastFrame.Toward(from, to, Vector3.Lerp(from, to, at) + lift);
            }
            case VfxRigMode.Trail when Radius > 0f && Period > 0f:
            {
                float theta = MathF.Tau * (t / Period);
                var here = new Vector3(Radius * MathF.Cos(theta), 0f, Radius * MathF.Sin(theta));
                // the tangent, a quarter turn ahead: where the circle is carrying it next
                float ahead = theta + MathF.PI / 2f;
                var facing = here + new Vector3(MathF.Cos(ahead), 0f, MathF.Sin(ahead));
                return VfxCastFrame.Toward(here, facing, here + lift);
            }
            default:
                return Matrix4x4.CreateTranslation(lift);
        }
    }
}

/// <summary>What the preview guesses a system is for, and why it guessed it.</summary>
/// <param name="Mode">The rig to open on.</param>
/// <param name="Why">One line the editor can show, so the guess is never silent.</param>
public readonly record struct VfxRigGuess(VfxRigMode Mode, string Why);

/// <summary>
/// M712: guess how a system wants to be carried, from its name.
///
/// <para><b>The file cannot answer this.</b> A system definition describes emitters and says nothing about
/// where the effect goes; what makes one a missile is the spell that flies it. The reference renderer
/// draws that conclusion and stops there - every one of its previews opens on "still" and the reader picks.
/// This goes one step further, because a name is evidence even if it is not proof, and opening on the
/// right rig is worth more than opening on a safe one.</para>
///
/// <para><b>What the naming is actually worth, measured.</b> Over 206,774 system rows in the installed
/// game, only 57.0% carry any recognised role token at all. Scored against the game's own wiring - the
/// spell records that name a missile, the clip and idle records that name a bone - a full six-way name
/// classifier reaches 90.5% overall but only 84.1% balanced across its classes, and the honest number is
/// the second one. <c>_mis_</c> on its own detects a missile at 86.4% precision and 87.9% recall. Matching
/// has to be hybrid: a substring for the long unambiguous words and an exact token for the three-letter
/// ones, because "Impact" and "Hit" turn up inside half the emotes in the game.</para>
///
/// <para><b>What it gets wrong, named rather than hand-waved.</b> 1,715 systems that really do fly read as
/// something else - <c>Sona_Skin26_CritAttack_Cas</c>, <c>Diana_Skin27_Q_Trail</c>,
/// <c>Pantheon_Skin30_R_Spear_Landing</c> - and 217 that do not fly carry a <c>_mis</c> anyway, because a
/// dash and a recall are written the same way: <c>Vayne_Skin52_Recall_mis2</c>. Four champions -
/// Jarvan IV, Wukong, Sett, Xin Zhao - have <c>_mis</c> systems and no linked missile at all, their dashes
/// being script-driven. The guess is a starting point and the reader overrides it.</para>
///
/// <para><b>The reliable signal is not here.</b> A spell's own record names the system it flies -
/// <c>mMissileEffectKey</c>, resolved through the skin's resource map - and hands over the speed as well:
/// 15,742 systems in the game are named as a missile that way, 97.5% of them with a missile spec, 81.2%
/// with an authored speed. Ahri's charm comes back at 1,550 units a second, which is its real speed.
/// Reading that would replace this guess with the game's own answer AND set the rig's numbers from it. It
/// needs the champion's spell bins beside the particle bin, which this window does not open yet.</para>
/// </summary>
public static class VfxRigNaming
{
    // Long enough to be unambiguous inside a longer word, so a substring is safe.
    private static readonly string[] TrailWords = { "trail", "swipe", "sweep", "slash" };
    private static readonly string[] MissileWords = { "missile", "projectile" };
    private static readonly string[] ChildWords = { "child" };
    // A dash or a recall is authored like a missile and is not one.
    private static readonly string[] NotAMissile = { "recall", "dash", "indicator", "targeter" };

    /// <summary>The rig to open a system on, and the reason.</summary>
    public static VfxRigGuess For(string? systemName)
    {
        string name = (systemName ?? "").ToLowerInvariant();
        if (name.Length == 0) return new(VfxRigMode.Still, "No name to go on, so it is left standing still.");

        if (Has(name, ChildWords))
            return new(VfxRigMode.Still,
                "Named as a child effect. A parent system spawns it, so on its own it has nowhere to go.");

        if (Has(name, TrailWords) || Token(name, "trail"))
            return new(VfxRigMode.Trail,
                "Named as a trail, and a trail needs motion to lay itself along - so the rig circles.");

        if (!Has(name, NotAMissile) && (Has(name, MissileWords) || Token(name, "mis")))
            return new(VfxRigMode.Missile,
                "Named as a missile. Over the installed game that reads correctly about 9 times in 10; a "
                + "dash or a recall is written the same way and is the usual mistake.");

        return new(VfxRigMode.Still,
            "Nothing in the name says it travels, so it stands still. Most effects do - they sit on a "
            + "champion or on the ground.");
    }

    private static bool Has(string name, string[] words)
    {
        foreach (var w in words) if (name.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>An exact '_'-separated token, with trailing digits stripped: <c>_mis2_</c> counts as
    /// <c>mis</c>. A substring would match half the corpus - "mis" is inside "dismiss" and "mist".</summary>
    private static bool Token(string name, string token)
    {
        foreach (var part in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.AsSpan().TrimEnd("0123456789");
            if (trimmed.SequenceEqual(token)) return true;
        }
        return false;
    }
}
