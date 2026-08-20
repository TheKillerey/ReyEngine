namespace ReyEngine.Formats.Particles;

/// <summary>One decoded entry: which section it came from, its key, and its raw bytes.</summary>
public sealed record TroyEntry(uint Key, int Section, byte[] Raw);

/// <summary>
/// The decoded <c>.troybin</c> body container (M422).
///
/// <para><b>Layout.</b> The body is a u16 presence mask followed, for each set bit from LSB to MSB, by
/// one self-describing section:</para>
/// <code>
///   u16 mask
///   for each set bit, low to high:
///       u16 count C
///       C x u32 key          (strictly ascending)
///       C x value            (width fixed per bit)
/// </code>
///
/// <para><b>How solid this is.</b> The forward parse consumes the body to the exact final byte in
/// <b>1,189 of 1,189</b> shipped files - zero slack, zero overrun - across 164 distinct masks and body
/// lengths 2..7,605. Three independent implementations reproduced it clean-room. Each per-bit width is
/// uniquely determined: an exhaustive search over widths 0..48 plus bit-packing, scored only on files
/// that actually set the bit, yields exactly one winner per bit (100% versus a best rival of 8.9%). All
/// 71 pairwise width swaps and every offsetting perturbation score at most 774/1,189, so the exact-total
/// match is genuine evidence rather than a degenerate fit. Section order is LSB-&gt;MSB (1,189 forward
/// versus 206 reversed). Entries are strictly ascending by key in 8,888 of 8,888 sections.</para>
///
/// <para><b>Field identity comes from the KEY, never from position.</b> The mask does not encode field
/// order or count - one mask (0x1FF6, 170 files) carries 79 distinct string-section counts. Typing is
/// value-adaptive: the same field lands in a different section depending on its value, which is why
/// mask-to-field-order correlations all failed.</para>
/// </summary>
public sealed class TroySections
{
    /// <summary>Bytes per value, indexed by bit. Bit 5 is one BIT per entry and is handled separately.</summary>
    private static readonly int[] Widths = { 4, 4, 1, 2, 1, 0, 3, 12, 2, 8, 4, 16, 2, 0, 0, 0 };

    private TroySections(ushort mask, IReadOnlyDictionary<uint, TroyEntry> byKey)
    {
        Mask = mask;
        ByKey = byKey;
    }

    public ushort Mask { get; }

    /// <summary>Every entry, keyed. Keys are unique within a file - 0 of 1,189 files repeat one.</summary>
    public IReadOnlyDictionary<uint, TroyEntry> ByKey { get; }

    /// <summary>
    /// Parse a body. Returns false rather than throwing on anything that does not consume exactly.
    /// <paramref name="consumed"/> reports how far the parse got, which is a useful corruption check:
    /// the value it implies for the header's string-block size agrees with the declared one in all
    /// 1,189 shipped files, so the two are mutually confirming.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out TroySections? sections, out int consumed)
    {
        sections = null;
        consumed = 0;
        if (body.Length < 2) return false;

        ushort mask = (ushort)(body[0] | (body[1] << 8));
        int p = 2;
        var byKey = new Dictionary<uint, TroyEntry>();

        for (int bit = 0; bit < 16; bit++)
        {
            if ((mask >> bit & 1) == 0) continue;
            // bits 13-15 are never set in any shipped file; refuse rather than invent a width
            if (bit > 12) return false;
            if (p + 2 > body.Length) return false;
            int count = body[p] | (body[p + 1] << 8);
            p += 2;

            if (p + 4 * count > body.Length) return false;
            var keys = new uint[count];
            for (int i = 0; i < count; i++)
            {
                int k = p + 4 * i;
                keys[i] = (uint)(body[k] | (body[k + 1] << 8) | (body[k + 2] << 16) | (body[k + 3] << 24));
            }
            p += 4 * count;

            if (bit == 5)
            {
                // one BIT per entry, LSB-first, zero-padded to a byte boundary (verified: 1,064 of
                // 1,064 sections with a partial final byte have all padding bits clear)
                int need = (count + 7) / 8;
                if (p + need > body.Length) return false;
                for (int i = 0; i < count; i++)
                {
                    byte v = (byte)(body[p + (i >> 3)] >> (i & 7) & 1);
                    byKey[keys[i]] = new TroyEntry(keys[i], bit, new[] { v });
                }
                p += need;
            }
            else
            {
                int w = Widths[bit];
                if (w == 0 || p + w * count > body.Length) return false;
                for (int i = 0; i < count; i++)
                    byKey[keys[i]] = new TroyEntry(keys[i], bit, body.Slice(p + w * i, w).ToArray());
                p += w * count;
            }
        }

        consumed = p;
        if (p != body.Length) return false;
        sections = new TroySections(mask, byKey);
        return true;
    }

    public bool TryGet(uint key, out TroyEntry entry) => ByKey.TryGetValue(key, out entry!);

    /// <summary>A two-component field: section 9 is 2xf32 raw, section 8 is 2xu8 tenths, and the string
    /// block carries the integer form. Probability-table keys are stored this way.</summary>
    public bool TryGetVector2(uint key, Func<int, string?>? resolveString, out float a, out float b)
    {
        a = b = 0f;
        if (!ByKey.TryGetValue(key, out var e)) return false;
        switch (e.Section)
        {
            case 9:
                a = BitConverter.ToSingle(e.Raw, 0);
                b = BitConverter.ToSingle(e.Raw, 4);
                return true;
            case 8:
                a = e.Raw[0] / 10f;
                b = e.Raw[1] / 10f;
                return true;
            case 12 when resolveString is not null:
            {
                string? s = resolveString(e.Raw[0] | (e.Raw[1] << 8));
                if (s is null) return false;
                var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var c = System.Globalization.CultureInfo.InvariantCulture;
                if (parts.Length != 2
                    || !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, c, out a)
                    || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, c, out b))
                    return false;
                return true;
            }
            default: return false;
        }
    }

    /// <summary>
    /// A three-component field, in world units.
    ///
    /// <para>The same ladder as the scalars, one arity up: section 7 is 3xf32 raw, section 6 is 3xu8
    /// DIVIDED BY 10, and the string block carries the integer form as <c>"x y z"</c> (98.3% of
    /// three-token strings are all-integer, because no raw multi-component byte section exists).</para>
    ///
    /// <para><b>Section 6 is tenths, and the witness is unanswerable:</b> <c>*e-rotation1-axis</c> is
    /// stored as <c>(0,10,0)</c> 261 times and as the string <c>"0 1 0"</c> 117 times. A rotation axis
    /// is a unit vector, so 10 means 1.0. <c>*p-drag</c> agrees - <c>(10,10,10)</c> x135 against
    /// <c>"1 1 1"</c> x36 - as does <c>*p-offset</c> with <c>(0,100,0)</c> against <c>"0 10 0"</c>.
    /// An earlier version of this reader refused section 6 on the grounds that tenths-versus-raw was
    /// unresolved, which discarded 30.6% of all vec3 entries; the median comparison behind that
    /// decision was a selection artefact, since truncating section 6 at 25.5 mechanically raises the
    /// f32 median.</para>
    ///
    /// <para>A scalar authored for a vector field is promoted to <c>(v,v,v)</c>. That is not a
    /// convenience: <c>*p-scale</c> has 954 scalar entries of 4,042, <c>*p-quadrot</c> 503 of 2,721.</para>
    /// </summary>
    public bool TryGetVector3(uint key, Func<int, string?>? resolveString, out System.Numerics.Vector3 value)
    {
        value = default;
        if (!ByKey.TryGetValue(key, out var e)) return false;

        if (e.Section == 7)
        {
            value = new System.Numerics.Vector3(
                BitConverter.ToSingle(e.Raw, 0),
                BitConverter.ToSingle(e.Raw, 4),
                BitConverter.ToSingle(e.Raw, 8));
            return true;
        }
        if (e.Section == 6)
        {
            value = new System.Numerics.Vector3(e.Raw[0] / 10f, e.Raw[1] / 10f, e.Raw[2] / 10f);
            return true;
        }
        // a scalar standing in for a vector - same ladder, promoted
        if (e.Section is 1 or 2 or 3 or 4 or 5 && TryGetScalar(key, out float promoted))
        {
            value = new System.Numerics.Vector3(promoted, promoted, promoted);
            return true;
        }
        if (e.Section == 12 && resolveString is not null)
        {
            string? s = resolveString(e.Raw[0] | (e.Raw[1] << 8));
            if (s is null) return false;
            var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // invariant on purpose: the development locale is German, where "0.4" would parse as 4
            var c = System.Globalization.CultureInfo.InvariantCulture;

            // M532: a section-12 value can be a SINGLE numeric token standing in for a uniform vector,
            // and refusing it here was the largest silent loss in the reader. *p-scale is authored this
            // way on 7,081 of 22,180 emitters that have one - the string "20", "100" and so on - and
            // TryGetScalar reads them all correctly. Only TryGetVector3 refused, so ScaleVector came back
            // null and the converter substituted an invented 50.
            //
            // Riot's own conversions confirm the promotion is uniform: LavaCauldron/Smoke's "20" is
            // (20,20,20) in Jade_LavaCauldron, GemGlow/glow's "100" is (100,100,100) in Jade_GemGlow.
            //
            // Sections 1-5 already promote a scalar this way just below; this is the same rule reaching
            // the one encoding that spells the number instead of packing it. Blast radius measured over
            // the whole corpus: *p-scale +7,081 and *p-xscale +923 newly read, and every other vec3 field
            // the reader asks for gains 0 or 2.
            if (parts.Length == 1)
            {
                if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, c, out float uniform))
                    return false;
                value = new System.Numerics.Vector3(uniform, uniform, uniform);
                return true;
            }

            if (parts.Length != 3) return false;
            if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, c, out float x)
                || !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, c, out float y)
                || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, c, out float z))
                return false;
            value = new System.Numerics.Vector3(x, y, z);
            return true;
        }
        return false;
    }

    /// <summary>The u16 string-block offset a string-valued entry (section 12) points at.</summary>
    /// <summary>
    /// A four-component field (M521). One arity up again: section 11 is 4xf32 raw, section 10 is
    /// 4xu8 TENTHS, and the string block carries it as four tokens.
    ///
    /// <para><b>Section 10 is tenths, measured over the corpus.</b> Read as f32 its bytes are absurd
    /// denormals - <c>0a0a0a0a</c> is 6.6e-33 - while as tenths the same bytes are
    /// <c>(1.0, 1.0, 1.0, 1.0)</c>, <c>0a020202</c> is <c>(1.0, 0.2, 0.2, 0.2)</c> and
    /// <c>06090a0a</c> is <c>(0.6, 0.9, 1.0, 1.0)</c>. 88% of components land in [0,1], which is what a
    /// (time, x, y, z) scale key should look like.</para>
    ///
    /// <para>Its dominant user is <c>*p-xscale{n}</c>, 9,985 of section 10's 21,796 entries.</para>
    /// </summary>
    public bool TryGetVector4(uint key, Func<int, string?>? resolveString,
        out System.Numerics.Vector4 value)
    {
        value = default;
        if (!ByKey.TryGetValue(key, out var e)) return false;
        switch (e.Section)
        {
            case 11:
                value = new System.Numerics.Vector4(
                    BitConverter.ToSingle(e.Raw, 0), BitConverter.ToSingle(e.Raw, 4),
                    BitConverter.ToSingle(e.Raw, 8), BitConverter.ToSingle(e.Raw, 12));
                return true;
            case 10:
                value = new System.Numerics.Vector4(
                    e.Raw[0] / 10f, e.Raw[1] / 10f, e.Raw[2] / 10f, e.Raw[3] / 10f);
                return true;
            case 12 when resolveString is not null:
            {
                string? text = resolveString(e.Raw[0] | (e.Raw[1] << 8));
                if (text is null) return false;
                var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 4) return false;
                var c = System.Globalization.CultureInfo.InvariantCulture;
                var n = System.Globalization.NumberStyles.Float;
                if (!float.TryParse(parts[0], n, c, out float x) || !float.TryParse(parts[1], n, c, out float y)
                    || !float.TryParse(parts[2], n, c, out float z) || !float.TryParse(parts[3], n, c, out float w))
                    return false;
                value = new System.Numerics.Vector4(x, y, z, w);
                return true;
            }
            default: return false;
        }
    }

    public bool TryGetStringOffset(uint key, out int offset)
    {
        offset = 0;
        if (!ByKey.TryGetValue(key, out var e) || e.Section != 12) return false;
        offset = e.Raw[0] | (e.Raw[1] << 8);
        return true;
    }

    /// <summary>
    /// A scalar field as a float.
    ///
    /// <para><b>Scaling belongs to the SECTION, not to the field.</b> An earlier reading of this format
    /// had it the other way round, with a per-field allowlist of "tenths" fields; that was wrong and the
    /// encoder turns out to be a plain minimal-width ladder keyed on the authored literal:</para>
    ///
    /// <list type="table">
    ///   <item><term>integer 0 or 1</term><description>section 5, one bit, raw</description></item>
    ///   <item><term>integer 2..255</term><description>section 4, u8, RAW</description></item>
    ///   <item><term>integer outside that</term><description>section 3, i16, raw</description></item>
    ///   <item><term>decimal n/10, n &lt;= 255</term><description>section 2, u8, DIVIDED BY 10</description></item>
    ///   <item><term>anything else</term><description>section 1, f32, raw</description></item>
    /// </list>
    ///
    /// <para><b>Two falsifiable predictions, both measured to hold corpus-wide.</b> Section 4 holds a 0
    /// or a 1 in exactly <b>0 of 7,387</b> entries (its minimum value is 2) - because those go to the
    /// one-bit section instead. Section 3 holds a value in 2..255 in exactly <b>0 of 1,187</b> entries -
    /// because section 4 already covers that range. Neither would be true of a per-field scheme.</para>
    ///
    /// <para>The old per-field allowlist applied its /10 to sections 2 AND 4 alike, which decoded 1,905
    /// section-4 entries ten times too small - 979 emission rates, 428 scales, 190 particle lifetimes.
    /// The apparent per-field evidence was a confound: pooling sections 2 and 4 as "u8" mixed a tenths
    /// section with a raw one.</para>
    /// </summary>
    /// <param name="resolveString">M520: needed for section 12, which stores numbers as TEXT.
    /// Leaving it out costs 101,530 readable values across the 5,851-file corpus - among them
    /// <c>e-rate=100</c> and <c>f-radius=200</c>, both confirmed against the FireTorch_Simple
    /// text/binary twin. Typing here is value-adaptive, so one field is spread over many sections:
    /// <c>e-rate</c> alone lands in 1, 2, 3, 4, 5 and 12. A reader that declines a section does not
    /// read that field badly, it reads it as ABSENT - which is worse, because absent means "the author
    /// left the default" to everything downstream.</param>
    public bool TryGetScalar(uint key, Func<int, string?>? resolveString, out float value)
    {
        value = 0f;
        if (!ByKey.TryGetValue(key, out var e)) return false;
        switch (e.Section)
        {
            case 1: value = BitConverter.ToSingle(e.Raw, 0); return true;
            case 2: value = e.Raw[0] / 10f; return true;       // decimal tenths
            case 3: value = BitConverter.ToInt16(e.Raw, 0); return true;
            case 4: value = e.Raw[0]; return true;             // integer, raw
            case 5: value = e.Raw[0]; return true;             // 0 or 1
            case 12 when resolveString is not null:
            {
                string? text = resolveString(e.Raw[0] | (e.Raw[1] << 8));
                if (text is null) return false;
                // ONE token only: "0.0 30" is a probability key, not a scalar, and taking its first
                // number as the whole value is how a curve silently becomes a constant
                var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 1
                       && float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out value);
            }
            default: return false;
        }
    }

    /// <summary>Scalar without a string resolver - sections 1-5 only. Kept for callers that genuinely
    /// have no string block to hand.</summary>
    public bool TryGetScalar(uint key, out float value) => TryGetScalar(key, null, out value);
}

/// <summary>
/// The key model. <c>key = sdbm-65599(lowercase(emitterName + fieldName))</c>, field names beginning
/// with <c>'*'</c>.
///
/// <para><b>This is the strongest result in the whole investigation.</b> Using only literal English
/// field-name guesses - no fitting - 58,614 keys resolve. The null controls are decisive: shuffling
/// emitter names across the corpus drops it to 0.89%, random same-length names to <b>exactly zero</b>,
/// and every alternative multiplier (31, 33, 131, 65539, 16777619) to exactly zero. It is a derivation,
/// not a corpus fit.</para>
///
/// <para>Because the key contains the emitter name, a field belongs to a specific emitter <i>by
/// construction</i>. That replaces every filename-similarity heuristic the converter used to need.</para>
/// </summary>
public static class TroyHash
{
    public static uint Sdbm(string s, uint h = 0)
    {
        foreach (char c in s) h = h * 65599 + c;
        return h;
    }

    /// <summary>Key for <paramref name="field"/> on <paramref name="emitterName"/>.</summary>
    public static uint FieldKey(string emitterName, string field) =>
        Sdbm(field.ToLowerInvariant(), Sdbm(emitterName.ToLowerInvariant()));

    /// <summary>The section every system-level field lives on. M520: recovered - it is literally
    /// <c>System</c>.</summary>
    public const string SystemSection = "System";

    /// <summary>Key for <paramref name="field"/> on the <c>[System]</c> section.</summary>
    public static uint SystemKey(string field) => FieldKey(SystemSection, field);

    /// <summary>
    /// The system-level chain that names emitter <paramref name="index"/> (1-based).
    ///
    /// <para>M520: the prefix is no longer a magic number. It is <c>System</c> + <c>*grouppart</c> -
    /// <c>Sdbm("*grouppart", Sdbm("system"))</c> is exactly the 0xAE671AB7 this used to carry as an
    /// unexplained seed. Recovered from the one shipped text/binary twin, FireTorch_Simple, whose
    /// <c>[System]</c> section spells the list out as GroupPart1..5 with a Type and an Importance
    /// beside each.</para>
    ///
    /// <para>The index is appended as DECIMAL DIGITS, which matters: a naive "0x0616933A + i" walk
    /// stops at 19 and silently loses 71 emitters across 7 files, collapsing binding in the largest
    /// effects from 79.7% to 50.8%.</para>
    /// </summary>
    public static uint EmitterNameKey(int index) => SystemKey(TroyFields.GroupPart(index));
}

/// <summary>
/// The legacy field names recovered from the corpus, with the scaling each one uses.
///
/// <para>Only fields whose scaling was verified against the corpus are listed. A field absent here is
/// read raw rather than guessed at, because a wrong /10 is silently ten times off rather than visibly
/// broken.</para>
/// </summary>
public static class TroyFields
{
    public const string Texture = "*p-texture";          // diffuse sprite: 3,306 TEX vs 28 RAMP
    public const string ColorTexture = "*p-rgba";        // colour ramp: 959 RAMP vs 632 TEX
    public const string TextureMult = "*p-texture-mult";
    public const string Mesh = "*p-mesh";                // 666 MESH, 0 non-mesh
    public const string Skin = "*p-skin";
    public const string MeshTexture = "*p-meshtex";
    public const string EmitterRate = "*e-rate";
    public const string EmitterLife = "*e-life";
    public const string ParticleLife = "*p-life";
    public const string Scale = "*p-scale";
    public const string Velocity = "*p-vel";
    public const string Drag = "*p-drag";
    public const string Acceleration = "*p-accel";
    public const string NumFrames = "*p-numframes";
    public const string FrameRate = "*p-framerate";
    public const string StartFrame = "*p-startframe";
    /// <summary>M429: the flipbook ATLAS GRID - (2,2) means a 2x2 sheet of 4 frames. Present on 1,487
    /// emitters. Without it the renderer's texDiv defaults to (1,1) and samples the whole sheet as one
    /// frame, so numFrames alone animates nothing.</summary>
    public const string TexDiv = "*p-texdiv";
    public const string ParticleType = "*p-type";
    /// <summary>M527: the camera trail's tiling size, becoming
    /// <c>primitive.mTrail.mBirthTilingSize</c> (6/6 against Riot's conversion).</summary>
    public const string TileSize = "*e-tilesize";
    public const string QuadRotation = "*p-quadrot";
    public const string RotationVelocity = "*p-rotvel";
    // measured: "*p-bindweight" resolves 0 keys in 0 files; the real name is *p-bindtoemitter
    // (785 files, 2,443 keys). A bind weight of 1 pins particles to the emitter and cancels velocity.
    public const string BindToEmitter = "*p-bindtoemitter";
    // M423: three-component motion and shape fields, measured to be genuine vec3s
    public const string Velocity3 = "*p-vel";
    public const string Acceleration3 = "*p-accel";
    public const string WorldAcceleration3 = "*p-worldaccel";
    public const string Offset3 = "*p-offset";
    public const string Drag3 = "*p-drag";
    public const string OrbitalVelocity3 = "*p-orbitvel";
    /// <summary>M427: emitter-space rotations. *e-rotation{n} is the angle, *e-rotation{n}-axis the
    /// unit axis, *e-rotation{n}P{k} the probability table that randomises it (typically 0..360).</summary>
    public static string EmitRotation(int n) => "*e-rotation" + N(n);
    public static string EmitRotationAxis(int n) => EmitRotation(n) + "-axis";

    // ---- M520: the rest of the vocabulary, from a text/binary twin ------------------------------
    //
    // K:\LeagueSandbox\League_Sandbox_Client\DATA\Particles ships FireTorch_Simple.troy AND
    // FireTorch_Simple.troybin - the same effect in both the authored text form and the shipped
    // binary. That pair is self-verifying: a name is only accepted here if sdbm("*"+name,
    // sdbm(section)) lands on a key that actually exists in the binary, and a wrong name cannot.
    // 250 of the text's 257 assignments do; the 7 that do not are marked ";UNKNOWN_HASH <n>" in the
    // text itself. Corpus effect: key coverage over all 5,851 files rises from 31.1% to 58.6%.

    // --- [System] ---
    /// <summary>Emitter <paramref name="n"/> of the system, 1-based. Lives on the section literally
    /// named <c>System</c>.</summary>
    public static string GroupPart(int n) => "*grouppart" + N(n);
    /// <summary>Quality tier - Simple / High / Low / Basic / Medium. The game drops emitters by this
    /// on lower settings, so ignoring it promotes every "Low" emitter to always-on.</summary>
    public static string GroupPartType(int n) => GroupPart(n) + "type";
    public static string GroupPartImportance(int n) => GroupPart(n) + "importance";
    public const string SimulateEveryFrame = "*simulateeveryframe";

    // --- emitter-level ---
    public const string EmitterActive = "*e-active";
    /// <summary>How long the emitter keeps emitting after being told to stop.</summary>
    public const string EmitterLinger = "*e-linger";
    public const string EmitterLocalOrient = "*e-local-orient";
    public const string EmitterPeriod = "*e-period";

    // --- particle-level ---
    public const string NormalMap = "*p-normal-map";
    public const string ColorOffset = "*p-coloroffset";
    public const string ColorScale = "*p-colorscale";
    public const string ColorType = "*p-colortype";
    public const string DistortionMode = "*p-distortion-mode";
    public const string DistortionPower = "*p-distortion-power";
    public const string ParticleLinger = "*p-linger";
    /// <summary>An offset applied after emission rather than at birth - it moves the whole particle
    /// instead of seeding where it starts.</summary>
    public const string PostOffset = "*p-postoffset";
    public const string RandomStartFrame = "*p-randomstartframe";
    public const string ScaleBias = "*p-scalebias";
    public const string SimpleOrient = "*p-simpleorient";
    /// <summary>Non-uniform scale over life. Unlike every other curve in this format it is NOT a P{n}
    /// probability table: the keys are <c>*p-xscale1..4</c> and <c>*p-xscale</c> is the enable
    /// flag.</summary>
    public const string XScale = "*p-xscale";
    public static string XScaleKey(int n) => XScale + N(n);
    /// <summary>
    /// M525: colour over life. <c>*p-xrgba{n}</c> is a key of five numbers - <c>time r g b a</c> - and
    /// <c>*p-xrgba</c> is the vec4 multiplier, exactly parallel to the xscale pair.
    ///
    /// <para>The name was not guessed. sdbm is algebraically invertible, and two emitters sharing a
    /// field satisfy <c>k1 - k2 = (base1 - base2) * 65599^n</c>, which solves the field's LENGTH without
    /// knowing the field: 9, with the tail sums falling into runs that differ by 1, the signature of a
    /// numbered suffix. Sweeping every 9-character name of that shape against the solved hash leaves
    /// exactly one that means anything. It resolves 16,147 of the corpus's 23,559 five-number keys.</para>
    ///
    /// <para>A five-component value has no fixed-width section - the widths run 1, 2, 3, 4, 8, 12, 16 -
    /// so a colour key is always stored as text.</para>
    /// </summary>
    public const string ColorOverLife = "*p-xrgba";
    public static string ColorKey(int n) => ColorOverLife + N(n);
    /// <summary>Render-order bucket. -1 puts the emitter behind, 1 in front.</summary>
    public const string Pass = "*pass";
    public const string RenderMode = "*rendermode";

    // --- force fields: the reference on the emitter, then the field's own section ---
    /// <summary>Reference <paramref name="n"/> of <paramref name="kind"/>, e.g. <c>*field-drag-1</c>.
    /// The VALUE is the name of the section holding the field.</summary>
    public static string FieldRef(TroyFieldKind kind, int n) => "*field-" + Slug(kind) + "-" + N(n);
    public const string FieldAcceleration = "*f-accel";
    public const string FieldDirection = "*f-direction";
    public const string FieldDrag = "*f-drag";
    public const string FieldLocalSpace = "*f-localspace";
    public const string FieldPeriod = "*f-period";
    public const string FieldPosition = "*f-pos";
    public const string FieldRadius = "*f-radius";
    public const string FieldVelocityDelta = "*f-veldelta";
    /// <summary>M522: the noise field's per-axis weighting, e.g. (2,1,0) to swirl mostly on X and not
    /// at all on Z. Riot's converter writes it to <c>axisFraction</c> unchanged; measured exactly on
    /// PolenNoise (2,1,0), ManaSnowNoise (1,1,0) and NoiseField1 (0,1,0).</summary>
    public const string FieldAxisFraction = "*f-axisfrac";

    /// <summary>The spelling used inside a <c>field-*-{n}</c> key. Deliberately not
    /// <c>kind.ToString()</c>: the legacy names are abbreviated (<c>orbit</c>, not <c>orbital</c>) and
    /// a mismatch here silently drops every field of that kind.</summary>
    public static string Slug(TroyFieldKind kind) => kind switch
    {
        TroyFieldKind.Acceleration => "accel",
        TroyFieldKind.Attraction => "attract",
        TroyFieldKind.Drag => "drag",
        TroyFieldKind.Orbital => "orbit",
        TroyFieldKind.Noise => "noise",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static readonly IReadOnlyList<TroyFieldKind> FieldKinds = new[]
    {
        TroyFieldKind.Acceleration, TroyFieldKind.Attraction, TroyFieldKind.Drag,
        TroyFieldKind.Orbital, TroyFieldKind.Noise,
    };

    private static string N(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
