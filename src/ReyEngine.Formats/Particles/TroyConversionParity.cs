using System.Globalization;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Particles;

/// <summary>How a converted property compared against the reference one.</summary>
public enum TroyParityVerdict
{
    /// <summary>Both sides have it and the values match.</summary>
    Agreed,
    /// <summary>Both sides have it and the values differ.</summary>
    Differed,
    /// <summary>Only the reference has it - something the converter did not produce.</summary>
    ReferenceOnly,
    /// <summary>Only the conversion has it - something the converter invented or kept.</summary>
    ConvertedOnly,
}

/// <summary>One property path's tally across every emitter compared.</summary>
/// <param name="Path">Dotted path from the emitter root, e.g.
/// <c>birthVelocity.dynamics.probabilityTables[1].keyValues[0]</c>.</param>
public sealed record TroyParityField(string Path, int Agreed, int Differed, int ReferenceOnly, int ConvertedOnly)
{
    public int Compared => Agreed + Differed;
    public int Total => Compared + ReferenceOnly + ConvertedOnly;
    /// <summary>Share of the both-present comparisons that matched. 1 when nothing was comparable.</summary>
    public double Agreement => Compared == 0 ? 1.0 : (double)Agreed / Compared;
}

/// <summary>One disagreement, kept so a report can show what actually differed rather than only a count.</summary>
public sealed record TroyParityExample(string System, string Emitter, string Path,
    TroyParityVerdict Verdict, string? Reference, string? Converted);

public sealed record TroyParityReport(
    int Systems, int Emitters, IReadOnlyList<TroyParityField> Fields, IReadOnlyList<TroyParityExample> Examples)
{
    public int Agreed => Fields.Sum(f => f.Agreed);
    public int Differed => Fields.Sum(f => f.Differed);
    public int Compared => Agreed + Differed;
    public double Agreement => Compared == 0 ? 1.0 : (double)Agreed / Compared;

    /// <summary>The tally for one path, or null when nothing at that path was ever seen.</summary>
    public TroyParityField? Field(string path) =>
        Fields.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));

    /// <summary>Paths grouped by their first segment, which is the emitter field a reader thinks in.</summary>
    public IEnumerable<(string Field, int Agreed, int Differed)> ByTopLevelField() =>
        Fields.GroupBy(f => f.Path.Split('.', '[')[0])
              .Select(g => (g.Key, g.Sum(x => x.Agreed), g.Sum(x => x.Differed)))
              .OrderBy(x => x.Key, StringComparer.Ordinal);
}

/// <summary>
/// Diffs a converted <c>VfxSystemDefinitionData</c> against a reference one, property by property (M524).
///
/// <para><b>Why this is generic rather than a list of fields to check.</b> The enumerated version missed
/// a real defect for two milestones: it compared <c>probabilityTables[0]</c> and never the other two, so
/// a uniform spread written to all three axes instead of one looked like 100% agreement. A walk that
/// descends into everything cannot be wrong about what it forgot to look at - it can only be wrong about
/// what it finds, which is visible in the report.</para>
///
/// <para><b>The reference is Riot's own conversion.</b> They re-authored the legacy effects as modern
/// systems and shipped the result, so for the systems that exist under the same name in both there is a
/// right answer rather than a plausible one. That also sets the ceiling: Riot re-tuned values in the
/// decade since the legacy snapshot, so a disagreement is a lead to investigate, not automatically a
/// bug.</para>
///
/// <para>Paths are reported rather than just totals, because a single number cannot say WHICH field
/// regressed and the whole point is to be told.</para>
/// </summary>
public static class TroyConversionParity
{
    private static uint H(string s) => HashAlgorithms.Fnv1a(s);
    private static readonly uint EmitterList = H("complexEmitterDefinitionData");
    private static readonly uint EmitterName = H("emitterName");
    private static readonly uint ParticleName = H("particleName");

    /// <summary>Accumulates across many pairs; one instance covers a whole corpus run.</summary>
    public sealed class Accumulator
    {
        internal readonly Dictionary<string, (int A, int D, int R, int C)> Tally = new(StringComparer.Ordinal);
        internal readonly List<TroyParityExample> Examples = new();
        internal int Systems;
        internal int Emitters;

        /// <summary>Cap on retained examples. Kept so a corpus run does not accumulate a million strings;
        /// the counts stay exact either way.</summary>
        public int MaxExamples { get; init; } = 200;

        /// <summary>Relative tolerance for float comparison. 2% absorbs the tenths quantisation the legacy
        /// binary applies to many values without hiding a real difference.</summary>
        public float Tolerance { get; init; } = 0.02f;

        /// <summary>Paths whose disagreements are expected and should not be recorded as examples - asset
        /// paths, mainly, since the converter deliberately rehomes them under ASSETS/Legacy.</summary>
        public IReadOnlySet<string> IgnoredPaths { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        public TroyParityReport Build() => new(Systems, Emitters,
            Tally.Select(kv => new TroyParityField(kv.Key, kv.Value.A, kv.Value.D, kv.Value.R, kv.Value.C))
                 .OrderByDescending(f => f.Differed).ThenBy(f => f.Path, StringComparer.Ordinal).ToList(),
            Examples);
    }

    /// <summary>
    /// Compare one system pair, adding to <paramref name="acc"/>. Emitters are matched BY NAME, not by
    /// position: the converter emits them in the legacy group order and Riot's list order need not agree,
    /// so a positional match would report every field of every emitter as different.
    /// </summary>
    public static void Compare(BinTreeObject reference, BinTreeObject converted, Accumulator acc,
        Func<uint, string?>? resolveName = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(converted);
        ArgumentNullException.ThrowIfNull(acc);

        acc.Systems++;
        string system = reference.Properties.GetValueOrDefault(ParticleName) is BinTreeString sn ? sn.Value : "?";

        // A name is not unique: DestroyedBuilding_idle names two of its group parts "smoke", and several
        // effects repeat one. So each name holds a QUEUE and matches are consumed in order, which pairs
        // duplicates one to one instead of throwing or collapsing them onto the first.
        var ours = new Dictionary<string, Queue<BinTreeStruct>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Emitters(converted))
        {
            string n = NameOf(e);
            if (!ours.TryGetValue(n, out var q)) ours[n] = q = new Queue<BinTreeStruct>();
            q.Enqueue(e);
        }

        foreach (var refEmitter in Emitters(reference))
        {
            string name = NameOf(refEmitter);
            if (!ours.TryGetValue(name, out var queue) || queue.Count == 0) continue;
            acc.Emitters++;
            Walk(system, name, "", refEmitter, queue.Dequeue(), acc, resolveName);
        }
    }

    private static IEnumerable<BinTreeStruct> Emitters(BinTreeObject system) =>
        system.Properties.GetValueOrDefault(EmitterList) is BinTreeContainer c
            ? c.Elements.OfType<BinTreeStruct>()
            : Enumerable.Empty<BinTreeStruct>();

    private static string NameOf(BinTreeStruct emitter) =>
        emitter.Properties.GetValueOrDefault(EmitterName) is BinTreeString s ? s.Value : "(unnamed)";

    private static void Walk(string system, string emitter, string path,
        BinTreeProperty? a, BinTreeProperty? b, Accumulator acc, Func<uint, string?>? resolveName)
    {
        if (a is null && b is null) return;
        if (a is null) { Record(acc, path, TroyParityVerdict.ConvertedOnly, system, emitter, null, Text(b!)); return; }
        if (b is null) { Record(acc, path, TroyParityVerdict.ReferenceOnly, system, emitter, Text(a), null); return; }

        // Structural nodes: recurse rather than compare. Comparing them as text would turn one differing
        // leaf into "the whole struct differs", which says nothing about where to look.
        if (a is BinTreeStruct sa && b is BinTreeStruct sb)
        {
            foreach (uint key in sa.Properties.Keys.Union(sb.Properties.Keys))
                Walk(system, emitter, Join(path, resolveName?.Invoke(key) ?? $"0x{key:x8}"),
                    sa.Properties.GetValueOrDefault(key), sb.Properties.GetValueOrDefault(key), acc, resolveName);
            return;
        }
        if (a is BinTreeContainer ca && b is BinTreeContainer cb)
        {
            // EVERY index, not just the first. A container compared only at [0] is what hid a uniform
            // spread being written to all three axes instead of one (M523).
            for (int i = 0; i < Math.Max(ca.Elements.Count, cb.Elements.Count); i++)
                Walk(system, emitter, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]",
                    i < ca.Elements.Count ? ca.Elements[i] : null,
                    i < cb.Elements.Count ? cb.Elements[i] : null, acc, resolveName);
            return;
        }
        if (a is BinTreeOptional oa && b is BinTreeOptional ob)
        {
            Walk(system, emitter, path, oa.Value, ob.Value, acc, resolveName);
            return;
        }

        bool same = Same(a, b, acc.Tolerance);
        Record(acc, path, same ? TroyParityVerdict.Agreed : TroyParityVerdict.Differed,
            system, emitter, Text(a), Text(b));
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    private static void Record(Accumulator acc, string path, TroyParityVerdict verdict,
        string system, string emitter, string? reference, string? converted)
    {
        var t = acc.Tally.GetValueOrDefault(path);
        acc.Tally[path] = verdict switch
        {
            TroyParityVerdict.Agreed => (t.A + 1, t.D, t.R, t.C),
            TroyParityVerdict.Differed => (t.A, t.D + 1, t.R, t.C),
            TroyParityVerdict.ReferenceOnly => (t.A, t.D, t.R + 1, t.C),
            _ => (t.A, t.D, t.R, t.C + 1),
        };

        if (verdict == TroyParityVerdict.Agreed) return;
        if (acc.Examples.Count >= acc.MaxExamples) return;
        if (acc.IgnoredPaths.Contains(path)) return;
        acc.Examples.Add(new TroyParityExample(system, emitter, path, verdict, reference, converted));
    }

    /// <summary>Value equality, with a relative tolerance on floats. Strings compare ORDINAL-insensitive:
    /// asset paths differ in case across the two eras and a case-only difference is not a conversion
    /// error.</summary>
    private static bool Same(BinTreeProperty a, BinTreeProperty b, float tolerance)
    {
        static bool Near(float x, float y, float tol) =>
            Math.Abs(x - y) <= tol * Math.Max(1f, Math.Abs(y));

        return (a, b) switch
        {
            (BinTreeF32 x, BinTreeF32 y) => Near(x.Value, y.Value, tolerance),
            (BinTreeVector2 x, BinTreeVector2 y) => Near(x.Value.X, y.Value.X, tolerance)
                                                    && Near(x.Value.Y, y.Value.Y, tolerance),
            (BinTreeVector3 x, BinTreeVector3 y) => Near(x.Value.X, y.Value.X, tolerance)
                                                    && Near(x.Value.Y, y.Value.Y, tolerance)
                                                    && Near(x.Value.Z, y.Value.Z, tolerance),
            (BinTreeVector4 x, BinTreeVector4 y) => Vector4Near(x.Value, y.Value, tolerance),
            // M524: asset paths compare by FILE NAME. The converter deliberately rehomes legacy assets
            // under ASSETS/Legacy while Riot's live somewhere else entirely, so a full-path comparison
            // reports 226 differences that are all policy rather than mis-binding - and buries the ones
            // that are not. By filename, 223 of 228 agree, which is the number that says the emitter is
            // pointing at the right art.
            (BinTreeString x, BinTreeString y) when IsAssetPath(x.Value) && IsAssetPath(y.Value) =>
                string.Equals(Path.GetFileNameWithoutExtension(x.Value),
                    Path.GetFileNameWithoutExtension(y.Value), StringComparison.OrdinalIgnoreCase),
            (BinTreeString x, BinTreeString y) => string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(Text(a), Text(b), StringComparison.OrdinalIgnoreCase),
        };
    }

    private static bool IsAssetPath(string s) =>
        s.Contains('/') && s.LastIndexOf('.') > s.LastIndexOf('/');

    private static bool Vector4Near(Vector4 x, Vector4 y, float tol)
    {
        static bool N(float a, float b, float t) => Math.Abs(a - b) <= t * Math.Max(1f, Math.Abs(b));
        return N(x.X, y.X, tol) && N(x.Y, y.Y, tol) && N(x.Z, y.Z, tol) && N(x.W, y.W, tol);
    }

    /// <summary>A leaf rendered for the report. Invariant on purpose - the development locale is German,
    /// where a comma decimal would make two values out of one.</summary>
    private static string Text(BinTreeProperty p)
    {
        var c = CultureInfo.InvariantCulture;
        return p switch
        {
            BinTreeF32 f => f.Value.ToString("0.####", c),
            BinTreeU8 u => u.Value.ToString(c),
            BinTreeU16 u => u.Value.ToString(c),
            BinTreeU32 u => u.Value.ToString(c),
            BinTreeI16 i => i.Value.ToString(c),
            BinTreeI32 i => i.Value.ToString(c),
            BinTreeBool b => b.Value ? "true" : "false",
            BinTreeBitBool b => b.Value ? "true" : "false",
            BinTreeString s => s.Value,
            BinTreeVector2 v => $"({v.Value.X.ToString("0.###", c)}, {v.Value.Y.ToString("0.###", c)})",
            BinTreeVector3 v => $"({v.Value.X.ToString("0.###", c)}, {v.Value.Y.ToString("0.###", c)}, "
                                + $"{v.Value.Z.ToString("0.###", c)})",
            BinTreeVector4 v => $"({v.Value.X.ToString("0.###", c)}, {v.Value.Y.ToString("0.###", c)}, "
                                + $"{v.Value.Z.ToString("0.###", c)}, {v.Value.W.ToString("0.###", c)})",
            BinTreeStruct s => $"{{{s.Properties.Count} field(s)}}",
            BinTreeContainer k => $"[{k.Elements.Count} item(s)]",
            _ => p.Type.ToString(),
        };
    }
}
