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
    /// <summary>M535: a BARE Vector2 leaf, not wrapped in any Value* struct. Riot uses this for the
    /// colour-lookup pair and no ValueVector3 form of it exists in 49,936 shipped emitters.</summary>
    Vector2,
}

/// <summary>One measured legacy-to-modern field rule.</summary>
/// <param name="Legacy">The legacy field key, including its leading <c>*</c>.</param>
/// <param name="Modern">The modern property name.</param>
/// <param name="Evidence">Agreement with Riot's own conversion over the paired systems, as
/// "agree/total" - so a reader can see how well supported each row is without leaving the file.</param>
public sealed record TroyMapping(string Legacy, string Modern, TroyWrite Write, string Evidence)
{
    /// <summary>M535: the value Riot omits rather than writes. Only meaningful for
    /// <see cref="TroyWrite.Vector2"/>, where the identity is (1,1) for a scale and (0,0) for an offset -
    /// so "write when non-zero" is the wrong test for one of them.</summary>
    public System.Numerics.Vector2 OmitAt { get; init; }

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
        // M535: every particle entered its flipbook on frame 0, so a 16-frame torch flame marched through
        // the sheet in lockstep and visibly pulsed instead of churning. 12 of Map2's 43 emitters carry it.
        new TroyMapping("*p-randomstartframe", "isRandomStartFrame", TroyWrite.BitBool, "6/6"),
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
        // M535: a BARE Vector2, and read through TryGetVector2 - these are authored in section 8, which
        // neither TryGetVector3 nor TryGetScalar accepts, so the rule fired on 0.76% of the field and the
        // old "23/23" evidence could not be reproduced (measured 0 hits over 3,724 paired systems).
        new TroyMapping("*p-colorscale", "colorLookUpScales", TroyWrite.Vector2, "222/222")
            { OmitAt = System.Numerics.Vector2.One },
        new TroyMapping("*p-coloroffset", "colorLookUpOffsets", TroyWrite.Vector2, "7/7")
            { OmitAt = System.Numerics.Vector2.Zero },

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
    /// <param name="buildVector">M534: how to turn a modern field name and a constant into the property.
    /// The third argument is which axis an UNLETTERED probability table belongs on - 0 everywhere except
    /// the simpleorient-2 rotation, where the random draw is the yaw and not the fixed pitch (M535).
    /// The converter passes one that attaches the field's probability tables, which this table has no way
    /// to know about - without it birthRotation0 and birthRotationalVelocity0 are bare constants and every
    /// particle is born at the same angle with the same spin. Null keeps the plain constant.</param>
    public static void Apply(TroySections sections, Func<int, string?> resolveString, string emitter,
        ICollection<BinTreeProperty> into,
        Func<string, Vector3, int, BinTreeProperty>? buildVector = null)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(resolveString);
        ArgumentNullException.ThrowIfNull(into);

        // M534: *p-simpleorient decides birthRotation0, and nothing has ever read it. The constant has
        // existed since M520 with no caller, so a quad the artist laid FLAT ON THE GROUND came out
        // standing up - the reported "env_fog_green is like a mesh going in the height", and the same on
        // LavaCauldron's Surface.
        //
        // The rule, scored against Riot's own conversions over 1,561 paired emitters: an authored vec3
        // *p-quadrot always wins; failing that, simpleorient 2 means a -90 pitch about X (101/107, from
        // a baseline of 21/107) and simpleorient 3 means (0,180,90) (14/14). Values 0 and 1 occur on 237
        // corpus emitters but on ZERO paired ones, so there is no measurement behind them and they are
        // left writing nothing rather than guessed at.
        bool orientationHandled = false, spinHandled = false;
        uint orientKey = TroyHash.FieldKey(emitter, TroyFields.SimpleOrient);
        uint quadKey = TroyHash.FieldKey(emitter, TroyFields.QuadRotation);
        if (sections.ByKey.ContainsKey(orientKey)
            && sections.TryGetScalar(orientKey, resolveString, out float orient))
        {
            // An authored vec3 outranks it. ReadVector only calls something a vec3 when the file really
            // spells one, so a lone scalar in *p-quadrot does not qualify and falls through to the pitch.
            bool authoredVector = sections.ByKey.TryGetValue(quadKey, out var quadEntry)
                && (quadEntry.Section is 6 or 7
                    || (quadEntry.Section == 12 && !sections.TryGetScalar(quadKey, resolveString, out _)));

            if (!authoredVector && orient is 2f or 3f)
            {
                float spin = sections.TryGetScalar(quadKey, resolveString, out float q) ? q : 0f;
                var rotation = orient == 2f ? new Vector3(-90f, spin, 0f) : new Vector3(0f, 180f, 90f);
                // M535: with the quad laid flat, the -90 is a FIXED pitch and the random draw is the
                // yaw - so an unlettered table belongs on Y. Left on X it multiplies the pitch and the
                // quad tumbles, which is the same symptom M534 set out to fix, one axis over. Riot puts
                // it in slot 1 on 7 of 7 twins with this constant. The simpleorient-3 form is left on
                // axis 0 because no twin measures it.
                int spreadAxis = orient == 2f ? 1 : 0;
                into.Add(buildVector?.Invoke("birthRotation0", rotation, spreadAxis)
                    ?? new BinTreeEmbedded(H("birthRotation0"), H("ValueVector3"), new BinTreeProperty[]
                    {
                        new BinTreeVector3(H("constantValue"), rotation),
                    }));
                orientationHandled = true;

                // M536: the SPIN has to follow the same axis, and M535 only moved the rotation. Left on X
                // it drives the -90 PITCH, so a flat card rotates out of the ground plane for its whole
                // life - env_fog_green's cards turn up to +/-240 degrees over 8-24s, rolling from flat
                // through edge-on past vertical. That is the reported "planes floating as smoking".
                //
                // Riot's near-twin of this very file - Jade_WormFog/fogflat in Map453, same LoFog01
                // sprite, same simpleorient, same tables - writes (0, 1, 0) with the table in slot 1.
                // Across the paired corpus 0 of 31 simpleorient-2 emitters put the spin on axis 0.
                // Strictly gated on this branch: the 1,631 emitters WITHOUT simpleorient keep axis 0,
                // where their tables measure 148 to 23 in favour.
                if (orient == 2f)
                {
                    uint spinKey = TroyHash.FieldKey(emitter, TroyFields.RotationVelocity);
                    if (sections.ByKey.ContainsKey(spinKey))
                    {
                        float rate = sections.TryGetScalar(spinKey, resolveString, out float r) ? r : 0f;
                        into.Add(buildVector?.Invoke("birthRotationalVelocity0", new Vector3(0f, rate, 0f), 1)
                            ?? new BinTreeEmbedded(H("birthRotationalVelocity0"), H("ValueVector3"),
                                new BinTreeProperty[] { new BinTreeVector3(H("constantValue"), new Vector3(0f, rate, 0f)) }));
                        spinHandled = true;
                    }
                }
            }
        }

        // M535: *p-colortype chooses WHICH per-particle quantity indexes the colour-ramp texture we
        // already write as particleColorTexture. Nothing has ever read it - the constant has no caller -
        // so 21 of Map2's 43 emitters sample their ramp along the wrong axis and animate through the
        // wrong colours. It is one legacy key producing TWO bare U8 leaves, so it cannot be a Rules row.
        //
        // Riot omits each half at its default (X=1, Y=0), measured 763/764 and 740/764 over 3,724 paired
        // systems, and writes them unwrapped - never inside a Value* struct.
        uint colorTypeKey = TroyHash.FieldKey(emitter, TroyFields.ColorType);
        if (sections.ByKey.ContainsKey(colorTypeKey)
            && sections.TryGetVector2(colorTypeKey, resolveString, out float typeX, out float typeY))
        {
            if (typeX != 1f) into.Add(new BinTreeU8(H("colorLookUpTypeX"), (byte)Math.Clamp(typeX, 0f, 255f)));
            if (typeY != 0f) into.Add(new BinTreeU8(H("colorLookUpTypeY"), (byte)Math.Clamp(typeY, 0f, 255f)));
        }

        foreach (var rule in Rules)
        {
            uint key = TroyHash.FieldKey(emitter, rule.Legacy);
            if (!sections.ByKey.ContainsKey(key)) continue;
            // the block above already authored these; writing the rows too would emit them twice
            if (orientationHandled && rule.Modern == "birthRotation0") continue;
            if (spinHandled && rule.Modern == "birthRotationalVelocity0") continue;

            if (rule.Write is TroyWrite.Vector2)
            {
                if (!sections.TryGetVector2(key, resolveString, out float vx, out float vy)) continue;
                var pair = new System.Numerics.Vector2(vx, vy);
                if (pair == rule.OmitAt) continue;
                into.Add(new BinTreeVector2(H(rule.Modern), pair));
                continue;
            }

            if (rule.Write is TroyWrite.ValueVector3 or TroyWrite.ValueVector3Broadcast)
            {
                if (ReadVector(sections, resolveString, key, rule.Write) is not { } v) continue;
                if (v == Vector3.Zero && !rule.WriteZero) continue;
                into.Add(buildVector?.Invoke(rule.Modern, v, 0)
                    ?? new BinTreeEmbedded(H(rule.Modern), H("ValueVector3"), new BinTreeProperty[]
                    {
                        new BinTreeVector3(H("constantValue"), v),
                    }));
                continue;
            }

            // M535: sections 8 and 9 hold a PAIR, which TryGetScalar rejects outright - so *p-bindtoemitter
            // was invisible on 75% of the emitters that carry it (11,339 of 17,123). The first component is
            // the value; the second is a separate quantity and must not be broadcast over it.
            if (!sections.TryGetScalar(key, resolveString, out float n)
                && !sections.TryGetVector2(key, resolveString, out n, out _)) continue;
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
        // M532: a section-12 value counts as an AUTHORED vec3 only when it spells three numbers.
        // TryGetVector3 now promotes a lone token to (v,v,v), which is what *p-scale needs and what
        // Riot's own conversions confirm - but 8 of the rules below are measured ZERO-PADDED
        // (birthRotation0 at 220/227, EmitterPosition at 104/105), so letting the promotion through
        // here would quietly turn their scalar into a broadcast. TryGetScalar succeeds on a section-12
        // entry exactly when it is one token, which is what separates the two cases.
        if (sections.TryGetVector3(key, resolve, out var v)
            && sections.ByKey.TryGetValue(key, out var e)
            && (e.Section is 6 or 7 || (e.Section == 12 && !sections.TryGetScalar(key, resolve, out _))))
            return v;
        if (!sections.TryGetScalar(key, resolve, out float n))
        {
            // M535: a two-component section is a real authored value, not a miss. *p-uvscroll-rgb writes
            // (0, 0.3) here, and rejecting it dropped the scroll entirely.
            if (sections.TryGetVector2(key, resolve, out float a, out float b)) return new Vector3(a, b, 0f);
            return null;
        }
        return write == TroyWrite.ValueVector3Broadcast ? new Vector3(n, n, n) : new Vector3(n, 0f, 0f);
    }
}
