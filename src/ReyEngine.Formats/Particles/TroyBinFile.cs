using System.Globalization;
using System.Numerics;

namespace ReyEngine.Formats.Particles;

/// <summary>What a string in the block turned out to be. The buckets were measured over all 1,189
/// shipped files before the rules were written, not chosen by taste.</summary>
public enum TroyStringKind
{
    /// <summary>Anything left after the other rules: 5,711 strings, 1,905 distinct.</summary>
    EmitterName,
    /// <summary>Ends in .dds/.tga/.scb/.skn/.skl, or a <c>teamrecolor, a.dds, b.dds</c> list: 5,581.</summary>
    AssetPath,
    /// <summary>Starts with Play_ or Stop_: 403.</summary>
    SoundEvent,
    /// <summary>Every whitespace-separated token parses as a float: 17,768.</summary>
    NumericTuple,
    /// <summary>A engine keyword rather than a name. Measured by frequency: Simple 581, High 258,
    /// Low 53, clamp 22, Basic 17 - each appears alongside real names in the same file.</summary>
    Keyword,
}

public sealed record TroyString(int Offset, string Value, TroyStringKind Kind);

/// <summary>
/// The legacy <c>.troybin</c> particle format (1,189 files in DATA.wad.client, orphaned since M201).
///
/// <para><b>What is verified.</b> The file is BINARY - the earlier research note calling it a "pre-PROP
/// text format" was wrong. Byte 0 is a version, and it is <c>2</c> in all 1,189 files. Bytes 1-2 are a
/// u16 giving the size of a NUL-terminated string block at the END of the file; slicing on it lands
/// exactly on the first string in every file, and the string block ends in a NUL in all 1,189. Strings
/// are referenced by u16 offset into that block: in the smallest non-trivial file the offsets 0, 6, 13
/// and 58 found in the body land precisely on its four strings, and the final u16 of the body is a valid
/// string offset in 1,175 of 1,175 files that have one.</para>
///
/// <para><b>What is NOT decoded, and must not be invented.</b> The body between the header and the
/// string block holds the numeric parameters - lifetimes, emission rates, velocities, sizes. It contains
/// plain IEEE-754 little-endian floats (0.25, 120.0, 360.0, 90.0 and 0.005 all appear as clean
/// constants), but which float belongs to which field is unknown. Three hypotheses were tested and
/// killed, recorded here so they are not tried again:</para>
/// <list type="bullet">
///   <item>the recurring 4-byte groups are NOT known bin field-name hashes - 0 of the 400 most frequent
///   resolve against 523,284 known hashes;</item>
///   <item>the group that varies between two otherwise identical sound particles is NOT the FNV-1 or
///   FNV-1a hash of the Wwise event name - 0 matches across the 395 files carrying an event string;</item>
///   <item>the body is NOT an id-keyed record array - reading it as 6-byte records yields 36,841 distinct
///   candidate ids and needs 21,946 of them to cover 90% of the mass, which is float noise, not a field
///   table.</item>
/// </list>
/// <para>So <see cref="UndecodedBodyBytes"/> is reported rather than guessed at, and every consumer is
/// expected to say plainly that the numerics are defaults.</para>
/// </summary>
public sealed class TroyBinFile
{
    private static readonly string[] AssetExtensions = { ".dds", ".tga", ".scb", ".skn", ".skl", ".sco" };

    /// <summary>Frequency-measured engine keywords. Everything else in that bucket is a genuine emitter
    /// name, so this list is deliberately short: a name wrongly demoted to a keyword silently deletes an
    /// emitter, which is worse than carrying one extra.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // quality tiers and sampler state
        "Simple", "High", "Low", "Basic", "Medium", "clamp",
        // primitive types, in the same positional role as Simple. Measured: "mesh" appears in 7 files
        // and ALL 7 reference a mesh asset (no false positives); "trail" 25, "beam" 4. Left in the name
        // bucket they became phantom emitters.
        "mesh", "trail", "beam",
    };

    private TroyBinFile(int version, IReadOnlyList<TroyString> strings, int undecoded)
    {
        Version = version;
        Strings = strings;
        UndecodedBodyBytes = undecoded;
    }

    public int Version { get; }
    public IReadOnlyList<TroyString> Strings { get; }

    /// <summary>Size of the region whose field layout is unknown. Reported so callers can be honest
    /// about how much of the original effect did not survive the conversion.</summary>
    public int UndecodedBodyBytes { get; }

    public IReadOnlyList<string> EmitterNames => Of(TroyStringKind.EmitterName);
    public IReadOnlyList<string> SoundEvents => Of(TroyStringKind.SoundEvent);

    /// <summary>Every asset path, in file order, with <c>teamrecolor, a.dds, b.dds</c> already split and
    /// Riot's own <c>doesnotexist.*</c> placeholders dropped.</summary>
    public IReadOnlyList<string> AssetPaths { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Diffuse textures - everything image-shaped that is NOT a colour ramp.
    ///
    /// <para>The split is not cosmetic. The legacy pixel shader
    /// (<c>DATA/Shaders/HLSL/ParticleSystem/QUAD_PS.ps_2_0</c>) samples two textures and multiplies
    /// them: <c>out_color0 = input.color0 * tex2D(TEXTURE, uv0) * tex2D(PARTICLE_COLOR_TEXTURE, uv1)</c>.
    /// TEXTURE is the sprite, PARTICLE_COLOR_TEXTURE is a colour ramp. Handing a ramp to the modern
    /// <c>texture</c> field renders a near-white quad, which is exactly what the first in-game test
    /// showed.</para>
    /// </summary>
    public IEnumerable<string> TexturePaths => AssetPaths.Where(p => IsImage(p) && !IsColorRamp(p));

    /// <summary>The ramp half of that multiply. Riot's naming convention is a <c>color-</c> prefix:
    /// 695 of 4,822 texture references across 411 files.</summary>
    public IEnumerable<string> ColorRampPaths => AssetPaths.Where(p => IsImage(p) && IsColorRamp(p));

    private static bool IsImage(string p) =>
        p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
        || p.EndsWith(".tga", StringComparison.OrdinalIgnoreCase);

    private static bool IsColorRamp(string p)
    {
        string file = Path.GetFileName(p);
        return file.StartsWith("color-", StringComparison.OrdinalIgnoreCase)
               || file.StartsWith("color_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Simple (.scb/.sco) and skinned (.skn) meshes both count; 374 of 1,175 files reference
    /// one, 680 references in total, 58 of them skinned.</summary>
    public IEnumerable<string> MeshPaths =>
        AssetPaths.Where(p => p.EndsWith(".scb", StringComparison.OrdinalIgnoreCase)
                              || p.EndsWith(".sco", StringComparison.OrdinalIgnoreCase)
                              || p.EndsWith(".skn", StringComparison.OrdinalIgnoreCase));

    /// <summary>The .skl beside a skinned .skn. The modern mesh primitive wants both.</summary>
    public string? SkeletonPath =>
        AssetPaths.FirstOrDefault(p => p.EndsWith(".skl", StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<string> Of(TroyStringKind kind) =>
        Strings.Where(s => s.Kind == kind).Select(s => s.Value).ToArray();

    /// <summary>
    /// Colour-over-life keys, recovered from runs of 5-token numeric tuples (<c>t r g b a</c>).
    /// Both authored scales occur in the corpus - <c>0.000000 1.000000 ...</c> and
    /// <c>0.437 255 255 151 255</c> - so a run whose colour components exceed 1 is divided by 255. The
    /// time component is never rescaled.
    /// </summary>
    /// <summary>
    /// Colour-over-life curves, one per run of 5-token tuples.
    ///
    /// <para><b>A file carries several curves, not one.</b> Measured: of the 660 files with colour keys,
    /// only 223 form a single monotonic curve - 136 have two runs, 77 three, 99 four, and so on. Reading
    /// them as one list produced a non-monotonic time series (0…1, 0…1, 0…1) and handed the same mangled
    /// curve to every emitter, which is why converted effects came out white instead of their authored
    /// colour. A run ends where time stops increasing.</para>
    ///
    /// <para>Both authored scales occur, and the choice is made per run: a run whose colour components
    /// exceed 1 is divided by 255. The time component is never rescaled.</para>
    /// </summary>
    public IReadOnlyList<IReadOnlyList<(float Time, Vector4 Color)>> ColorCurves { get; private set; }
        = Array.Empty<IReadOnlyList<(float, Vector4)>>();

    /// <summary>Total keys across every curve - what the provenance line reports.</summary>
    public int ColorKeyCount => ColorCurves.Sum(c => c.Count);

    public static bool TryParse(byte[] data, out TroyBinFile? file, out string? error)
    {
        file = null;
        error = null;
        if (data is null || data.Length < 3) { error = "Not a .troybin: fewer than 3 bytes."; return false; }

        int version = data[0];
        if (version != 2)
        {
            // every one of the 1,189 shipped files is version 2; anything else is unverified territory
            error = $"Unsupported .troybin version {version} (every shipped file is version 2).";
            return false;
        }

        int blockSize = BitConverter.ToUInt16(data, 1);
        if (blockSize > data.Length - 3)
        { error = $"String block ({blockSize} bytes) does not fit in a {data.Length}-byte file."; return false; }

        int blockStart = data.Length - blockSize;
        var strings = new List<TroyString>();
        if (blockSize > 0)
        {
            int cursor = 0;
            while (cursor < blockSize)
            {
                int end = Array.IndexOf(data, (byte)0, blockStart + cursor, blockSize - cursor);
                if (end < 0) break;
                int length = end - (blockStart + cursor);
                if (length > 0)
                {
                    string value = System.Text.Encoding.ASCII.GetString(data, blockStart + cursor, length).Trim();
                    if (value.Length > 0) strings.Add(new TroyString(cursor, value, Classify(value)));
                }
                cursor += length + 1;
            }
        }

        var result = new TroyBinFile(version, strings, Math.Max(0, blockStart - 3));
        result.AssetPaths = ExtractAssets(strings);
        result.ColorCurves = ExtractColorCurves(strings);
        result.DecodeBody(data, blockStart);
        file = result;
        return true;
    }

    /// <summary>
    /// M422: the decoded body. Non-null means every field below was read by key rather than inferred,
    /// and <see cref="Emitters"/> is authoritative.
    /// </summary>
    public TroySections? Sections { get; private set; }

    /// <summary>
    /// Emitters read from the body by key. EMPTY when the body could not be decoded, in which case
    /// callers must fall back to the string-classification heuristics above.
    ///
    /// <para>This is the authoritative emitter list, and it disagrees with the heuristic in both
    /// directions - <c>firetorch_purple</c> has 4 emitters where string classification counted 11.</para>
    /// </summary>
    public IReadOnlyList<TroyEmitter> Emitters { get; private set; } = Array.Empty<TroyEmitter>();

    public bool HasDecodedBody => Sections is not null && Emitters.Count > 0;

    private void DecodeBody(byte[] data, int blockStart)
    {
        var body = data.AsSpan(3, Math.Max(0, blockStart - 3));
        if (!TroySections.TryParse(body, out var sections, out _) || sections is null) return;
        Sections = sections;

        var offsets = Strings.ToDictionary(s => s.Offset, s => s.Value);
        string? Str(string emitter, string field) =>
            sections.TryGetStringOffset(TroyHash.FieldKey(emitter, field), out int o)
            && offsets.TryGetValue(o, out var v) ? v : null;
        float? Num(string emitter, string field) =>
            sections.TryGetScalar(TroyHash.FieldKey(emitter, field), TroyFields.IsTenths(field), out float v)
                ? v : null;

        var emitters = new List<TroyEmitter>();
        for (int i = 1; i <= 64; i++)
        {
            if (!sections.TryGetStringOffset(TroyHash.EmitterNameKey(i), out int nameOffset)) break;
            if (!offsets.TryGetValue(nameOffset, out var name)) break;
            emitters.Add(new TroyEmitter(
                name,
                Str(name, TroyFields.Texture),
                Str(name, TroyFields.ColorTexture),
                Str(name, TroyFields.TextureMult),
                Str(name, TroyFields.Mesh) ?? Str(name, TroyFields.Skin),
                Num(name, TroyFields.EmitterRate),
                Num(name, TroyFields.ParticleLife),
                Num(name, TroyFields.EmitterLife),
                Num(name, TroyFields.Scale),
                (int?)Num(name, TroyFields.NumFrames),
                Num(name, TroyFields.FrameRate),
                (int?)Num(name, TroyFields.StartFrame),
                (int?)Num(name, TroyFields.ParticleType)));
        }
        Emitters = emitters;
    }

    private static TroyStringKind Classify(string value)
    {
        string low = value.ToLowerInvariant();
        if (low.StartsWith("teamrecolor", StringComparison.Ordinal)) return TroyStringKind.AssetPath;
        foreach (string ext in AssetExtensions)
            if (low.EndsWith(ext, StringComparison.Ordinal)) return TroyStringKind.AssetPath;
        if (low.StartsWith("play_", StringComparison.Ordinal) || low.StartsWith("stop_", StringComparison.Ordinal))
            return TroyStringKind.SoundEvent;
        if (IsNumericTuple(value, out _)) return TroyStringKind.NumericTuple;
        if (Keywords.Contains(value)) return TroyStringKind.Keyword;
        return TroyStringKind.EmitterName;
    }

    /// <summary>German locale is the development default, so every parse here is invariant on purpose -
    /// a comma-decimal reading would turn "0.5" into 5.</summary>
    private static bool IsNumericTuple(string value, out float[] parts)
    {
        parts = Array.Empty<float>();
        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;
        var parsed = new float[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            if (!float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
                return false;
        parts = parsed;
        return true;
    }

    private static IReadOnlyList<string> ExtractAssets(IEnumerable<TroyString> strings)
    {
        var list = new List<string>();
        foreach (var s in strings.Where(s => s.Kind == TroyStringKind.AssetPath))
            foreach (string piece in s.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                // "teamrecolor, a.dds, b.dds" carries the directive as its first token
                if (piece.Equals("teamrecolor", StringComparison.OrdinalIgnoreCase)) continue;
                // Riot's own "no asset here" placeholder - 3 of the 134 unresolvable paths in the corpus
                if (piece.StartsWith("doesnotexist", StringComparison.OrdinalIgnoreCase)) continue;
                if (!list.Contains(piece, StringComparer.OrdinalIgnoreCase)) list.Add(piece);
            }
        return list;
    }

    private static IReadOnlyList<IReadOnlyList<(float, Vector4)>> ExtractColorCurves(
        IEnumerable<TroyString> strings)
    {
        var curves = new List<List<(float, Vector4)>>();
        List<(float, Vector4)>? current = null;
        foreach (var s in strings.Where(s => s.Kind == TroyStringKind.NumericTuple))
        {
            if (!IsNumericTuple(s.Value, out var p) || p.Length != 5) continue;
            var key = (p[0], new Vector4(p[1], p[2], p[3], p[4]));
            // time not increasing means a new curve started
            if (current is null || key.Item1 <= current[^1].Item1)
            {
                current = new List<(float, Vector4)>();
                curves.Add(current);
            }
            current.Add(key);
        }

        foreach (var curve in curves)
        {
            bool scale255 = curve.Any(k => k.Item2.X > 1f || k.Item2.Y > 1f
                                           || k.Item2.Z > 1f || k.Item2.W > 1f);
            if (!scale255) continue;
            for (int i = 0; i < curve.Count; i++)
                curve[i] = (curve[i].Item1, curve[i].Item2 / 255f);
        }
        return curves.Select(c => (IReadOnlyList<(float, Vector4)>)c).ToArray();
    }
}
