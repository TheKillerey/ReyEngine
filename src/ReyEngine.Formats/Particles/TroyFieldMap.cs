using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Particles;

/// <summary>How a legacy value becomes a modern property.</summary>
public enum TroyWrite
{
    /// <summary>A packed bit. Present-and-non-zero means true; a legacy zero is OMITTED, not written
    /// false - measured on every flag in the table.</summary>
    BitBool,
    /// <summary>A bare unsigned byte, not a float and not wrapped.</summary>
    U8,
    /// <summary>A signed 16-bit leaf. Riot never uses U8 or a Value wrapper for these.</summary>
    I16,
    /// <summary>A plain f32 leaf.</summary>
    F32,
    /// <summary>An embedded ValueFloat carrying constantValue.</summary>
    ValueFloat,
    /// <summary>An embedded ValueVector3 carrying constantValue. A scalar legacy value is
    /// ZERO-PADDED into X, not broadcast across the axes.</summary>
    ValueVector3,
    /// <summary>An embedded ValueVector3 whose scalar promotes by BROADCAST (x,x,x) rather than by
    /// zero-padding - used where the legacy value is a uniform magnitude.</summary>
    ValueVector3Broadcast,
}

/// <summary>One measured legacy-to-modern field rule.</summary>
/// <param name="Legacy">The legacy field key, including its leading <c>*</c>.</param>
/// <param name="Modern">The modern property name.</param>
/// <param name="Evidence">Agreement with Riot's own conversion over the paired systems, as
/// "agree/total" - so a reader can see how well supported each row is without leaving the file.</param>
public sealed record TroyMapping(string Legacy, string Modern, TroyWrite Write, string Evidence)
{
    /// <summary>Write a legacy zero rather than omitting it. Nothing in the table sets this today;
    /// it exists because "omit the zero" is a measured rule per field, not a global one.</summary>
    public bool WriteZero { get; init; }
}

/// <summary>
/// The legacy-to-modern field table (M526).
///
/// <para>Every row was measured against Riot's own conversion of the same effects - the 197 systems
/// that exist under the same name in both corpora - and then re-measured by a second pass whose brief
/// was to refute it. The <c>Evidence</c> column carries the agreement so the support for each row is
/// visible here rather than buried in a commit message. Nothing lands in this table on a hunch: a
/// field whose source could not be established stays out, and the parity harness reports it as a
/// known gap instead.</para>
///
/// <para><b>Many of these names were recovered algebraically, not guessed.</b> sdbm satisfies
/// <c>sdbm(F, base) = base * 65599^n + sdbm(F)</c>, so two emitters sharing a field give
/// <c>k1 - k2 = (base1 - base2) * 65599^n</c>, which solves the field's LENGTH and hash without
/// knowing the field; sweeping names of that length then names it. That is how <c>*single-particle</c>,
/// <c>*uniformscale</c>, <c>*p-uvscroll-rgb</c>, <c>*e-alpharef</c>, <c>*e-timeoffset</c> and the rest
/// of the un-guessable ones below were found. Note that several break the <c>*p-</c>/<c>*e-</c>
/// convention entirely, which is why guessing had failed on them.</para>
///
/// <para><b>The zero rule is not decoration.</b> Riot omits a defaulted value rather than writing it,
/// and for the flags a legacy zero means "write nothing" - not "write false". Writing the zero would
/// differ from every shipped file.</para>
/// </summary>
public static class TroyFieldMap
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);

    /// <summary>
    /// Rules applied to every emitter. Ordered by how much of the corpus each touches, so the ones
    /// that matter most are the ones read first.
    /// </summary>
    public static readonly IReadOnlyList<TroyMapping> Rules = new[]
    {
        // ---- orientation and space --------------------------------------------------------------
        //
        // isLocalOrientation is deliberately NOT here. *e-local-orient looks like its source and
        // predicts it perfectly by presence (78 hits, 0 misses), but the sense is INVERTED: a legacy
        // 0 makes Riot write isLocalOrientation=false (78 of 78 on value), and a legacy 1 makes Riot
        // write NOTHING (18 of 18). So local orientation is the engine default and the property
        // exists only to switch it off. The half that stays unresolved is the omission: 23 legacy
        // zeros also get nothing, and until that is explained, writing the property on them would be
        // inventing 23 properties Riot chose not to write. Measured, recorded, not implemented.
        new TroyMapping("*p-local-orient", "particleIsLocalOrientation", TroyWrite.BitBool, "91/91"),
        new TroyMapping("*p-vecalign", "isDirectionOriented", TroyWrite.BitBool, "13/13"),

        // ---- flags whose names had to be solved, not guessed ------------------------------------
        // None of these carries a *p-/*e- prefix, which is why every guess missed them.
        new TroyMapping("*single-particle", "isSingleParticle", TroyWrite.BitBool, "122/122"),
        new TroyMapping("*uniformscale", "isUniformScale", TroyWrite.BitBool, "111/111"),
        new TroyMapping("*p-xquadrot-on", "isRotationEnabled", TroyWrite.BitBool, "27/27"),

        // ---- motion ------------------------------------------------------------------------------
        new TroyMapping("*p-bindtoemitter", "bindWeight", TroyWrite.ValueFloat, "254/254"),
        new TroyMapping("*p-quadrot", "birthRotation0", TroyWrite.ValueVector3, "220/227"),
        new TroyMapping("*p-rotvel", "birthRotationalVelocity0", TroyWrite.ValueVector3, "58/60"),
        new TroyMapping("*p-postoffset", "EmitterPosition", TroyWrite.ValueVector3, "104/105"),
        new TroyMapping("*p-xquadrot", "rotation0", TroyWrite.ValueFloat, "18/18"),
        new TroyMapping("*particle-velocity", "velocity", TroyWrite.ValueVector3, "16/16"),
        new TroyMapping("*particle-acceleration", "acceleration", TroyWrite.ValueVector3, "8/8"),

        // ---- texture animation -------------------------------------------------------------------
        new TroyMapping("*p-uvscroll-rgb", "birthUvScrollRate", TroyWrite.ValueVector3, "169/169"),
        new TroyMapping("*e-uvoffset", "birthUVOffset", TroyWrite.ValueVector3, "43/44"),

        // ---- colour lookup -------------------------------------------------------------------------
        new TroyMapping("*p-colorscale", "colorLookUpScales", TroyWrite.ValueVector3Broadcast, "23/23"),
        new TroyMapping("*p-coloroffset", "colorLookUpOffsets", TroyWrite.ValueVector3Broadcast, "7/7"),

        // ---- timing and render state ---------------------------------------------------------------
        new TroyMapping("*e-timeoffset", "timeBeforeFirstEmission", TroyWrite.F32, "50/51"),
        new TroyMapping("*e-alpharef", "alphaRef", TroyWrite.U8, "9/9"),
    };

    /// <summary>
    /// Append every rule that fires for one emitter.
    ///
    /// <para>A rule fires only when the legacy field is PRESENT and, unless the rule says otherwise,
    /// non-zero. Absence means the author left the default and Riot writes nothing - copying a zero
    /// there would differ from every shipped file.</para>
    /// </summary>
    public static void Apply(TroySections sections, Func<int, string?> resolveString, string emitter,
        ICollection<BinTreeProperty> into)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(resolveString);
        ArgumentNullException.ThrowIfNull(into);

        foreach (var rule in Rules)
        {
            uint key = TroyHash.FieldKey(emitter, rule.Legacy);
            if (!sections.ByKey.ContainsKey(key)) continue;

            if (rule.Write is TroyWrite.ValueVector3 or TroyWrite.ValueVector3Broadcast)
            {
                if (ReadVector(sections, resolveString, key, rule.Write) is not { } v) continue;
                if (v == Vector3.Zero && !rule.WriteZero) continue;
                into.Add(new BinTreeEmbedded(H(rule.Modern), H("ValueVector3"), new BinTreeProperty[]
                {
                    new BinTreeVector3(H("constantValue"), v),
                }));
                continue;
            }

            if (!sections.TryGetScalar(key, resolveString, out float n)) continue;
            if (n == 0f && !rule.WriteZero) continue;

            into.Add(rule.Write switch
            {
                // BitBool, not Bool: Riot packs these, and a Bool byte in that slot is a different
                // wire type - the client would read the property as malformed.
                TroyWrite.BitBool => new BinTreeBitBool(H(rule.Modern), true),
                TroyWrite.U8 => new BinTreeU8(H(rule.Modern), (byte)Math.Clamp(n, 0f, 255f)),
                TroyWrite.I16 => new BinTreeI16(H(rule.Modern), (short)Math.Clamp(n, short.MinValue, short.MaxValue)),
                TroyWrite.F32 => new BinTreeF32(H(rule.Modern), n),
                _ => new BinTreeEmbedded(H(rule.Modern), H("ValueFloat"), new BinTreeProperty[]
                {
                    new BinTreeF32(H("constantValue"), n),
                }),
            });
        }
    }

    /// <summary>
    /// A vec3 for a rule, accepting the scalar forms too.
    ///
    /// <para>The two promotions are NOT interchangeable and each was measured on its own field.
    /// <c>*p-quadrot</c> zero-pads - a scalar 45 becomes (45,0,0), which is a rotation about one axis -
    /// while a uniform magnitude such as <c>*p-colorscale</c> broadcasts to (x,x,x). Getting these the
    /// wrong way round is silent: both produce a valid vec3.</para>
    /// </summary>
    private static Vector3? ReadVector(TroySections sections, Func<int, string?> resolve, uint key,
        TroyWrite write)
    {
        if (sections.TryGetVector3(key, resolve, out var v)
            && sections.ByKey.TryGetValue(key, out var e) && e.Section is 6 or 7 or 12)
            return v;
        if (!sections.TryGetScalar(key, resolve, out float n)) return null;
        return write == TroyWrite.ValueVector3Broadcast ? new Vector3(n, n, n) : new Vector3(n, 0f, 0f);
    }
}
