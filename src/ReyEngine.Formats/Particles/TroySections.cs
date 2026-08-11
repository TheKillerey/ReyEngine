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
            if (parts.Length != 3) return false;
            // invariant on purpose: the development locale is German, where "0.4" would parse as 4
            var c = System.Globalization.CultureInfo.InvariantCulture;
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
    public bool TryGetScalar(uint key, out float value)
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
            default: return false;
        }
    }
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

    /// <summary>
    /// The system-level chain that names emitter <paramref name="index"/> (1-based).
    ///
    /// <para>The root is the sdbm of a prefix string whose literal spelling is still unrecovered; the
    /// hash itself is verified, reproducing the observed keys exactly (0x0616933A for emitter 1,
    /// 0x12C83B76 for 10). The index is appended as DECIMAL DIGITS, which matters: a naive
    /// "0x0616933A + i" walk stops at 19 and silently loses 71 emitters across 7 files, collapsing
    /// binding in the largest effects from 79.7% to 50.8%.</para>
    /// </summary>
    public static uint EmitterNameKey(int index) => Sdbm(index.ToString(System.Globalization.CultureInfo.InvariantCulture), 0xAE671AB7);
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
    public const string ParticleType = "*p-type";
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

}
