using System.Numerics;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Characters;
using ReyEngine.Formats.Meta;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M826: the census the M813 review asked for before C4 writes anything - over the skins of a set of champions, the colours of the systems each skin REACHES:
/// how many carry probability tables, how many emitters multiply two coloured factors, how many keys a hue shift would bend between. Opt-in
/// (<c>REYENGINE_CENSUS=1</c>): it informs the product and key rules documented in docs/plans/chroma-studio.md and is not a regression test.
/// </summary>
public sealed class EffectColorCensusTests
{
    private readonly ITestOutputHelper _output;
    public EffectColorCensusTests(ITestOutputHelper output) => _output = output;

    private const string Champions = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions";

    private static readonly Lazy<HashDatabase?> Database = new(() => { try { return new HashSyncService().LoadLocal(_ => { }); } catch { return null; } });

    [Fact]
    public void EffectColourCensus()
    {
        if (System.Environment.GetEnvironmentVariable("REYENGINE_CENSUS") != "1")
        {
            _output.WriteLine("SKIPPED: set REYENGINE_CENSUS=1 to run the M826 effect colour census.");
            return;
        }
        if (!Directory.Exists(Champions) || Database.Value is not { } db) return;
        var names = (System.Environment.GetEnvironmentVariable("REYENGINE_CENSUS_CHAMPIONS") ?? "Lillia,Ahri,Aatrox,Lux,Jinx,Kalista,Yone,Seraphine,Ezreal,Jhin")
            .Split(',', StringSplitOptions.RemoveEmptyEntries);

        var total = new Tally();
        foreach (var champion in names)
        {
            string wad = Path.Combine(Champions, champion + ".wad.client");
            if (!File.Exists(wad)) continue;
            using var archive = WadArchive.Open(wad, new WadPathResolver(db));
            byte[]? Read(string path)
            {
                try { ulong h = BinTexturePath.HashOfReference(path); return archive.TryGetEntry(h, out _) ? archive.Extract(h) : null; }
                catch { return null; }
            }
            var scanner = new SkinColorScanner();
            var binCache = new Dictionary<string, EffectColorScan>(StringComparer.OrdinalIgnoreCase);
            var tally = new Tally();
            int skins = 0;
            foreach (var skinBin in archive.Entries.Where(e => e.IsResolved && SkinColorScanner.TryParseSkinPath(e.Path, out string f, out _)
                                                          && f.Equals(champion, StringComparison.OrdinalIgnoreCase)).Select(e => e.Path).OrderBy(p => p))
            {
                var inventory = scanner.Scan(new SkinColorRequest(skinBin, Read,
                    h => db.TryGetBinName(h, out var n) ? n : null, h => db.TryGetPath(h, out var p) ? p : null, IncludeSharing: false));
                if (inventory.Effects.Count == 0) continue;
                skins++;
                foreach (var group in inventory.Effects.GroupBy(e => e.Bin, StringComparer.OrdinalIgnoreCase))
                {
                    if (!binCache.TryGetValue(group.Key, out var scan))
                    {
                        var bytes = Read(group.Key);
                        binCache[group.Key] = scan = bytes is null ? new EffectColorScan(Array.Empty<EffectColorField>(), Array.Empty<EffectExcludedField>())
                                                                  : SkinEffectColors.Read(bytes, includeExcluded: false);
                    }
                    var reached = group.Select(g => g.PathHash).ToHashSet();
                    tally.Add(scan.Fields.Where(f => reached.Contains(f.Key.System)).ToList(), inventory.Effects.Where(e => reached.Contains(e.PathHash)).ToList());
                }
            }
            _output.WriteLine($"== {champion}: {skins} skin(s) with effects");
            _output.WriteLine(tally.Report());
            total.Merge(tally);
        }
        _output.WriteLine("== ALL");
        _output.WriteLine(total.Report());
    }

    private sealed class Tally
    {
        public int Systems, Emitters, Fields, Constant, Curve, KeysTotal, Hdr, Negative, NotRecolourable;
        public readonly Dictionary<string, int> ByField = new();
        public readonly Dictionary<EffectProbability, int> Prob = new();
        public readonly Dictionary<string, int> ProbByField = new();
        public int EmittersWithTwoFactors, EmittersWithTwoColoured, EmittersNoValueFactor, EmittersOnlyAlphaCurves;
        public int SegmentsBothColoured;
        public readonly Dictionary<int, int> Bent = new(), BentTight = new();
        public int ColouredCarrierOther;   // emitter: carrier is birth while life has colour too

        private readonly HashSet<(uint, int)> _seen = new();
        private readonly HashSet<uint> _systemsSeen = new();

        public void Add(IReadOnlyList<EffectColorField> fields, IReadOnlyList<EffectSystemEntry> systems)
        {
            foreach (var s in systems) if (_systemsSeen.Add(s.PathHash)) Systems++;
            foreach (var emitter in fields.GroupBy(f => (f.Key.System, f.Key.Emitter)))
            {
                if (!_seen.Add(emitter.Key)) continue;
                Emitters++;
                var fs = emitter.ToList();
                foreach (var f in fs)
                {
                    Fields++;
                    ByField[f.Key.Field] = ByField.GetValueOrDefault(f.Key.Field) + 1;
                    if (f.IsCurve) { Curve++; KeysTotal += f.Keys.Count; } else Constant++;
                    if (f.Values.Any(v => Math.Max(v.X, Math.Max(v.Y, v.Z)) > 1f)) Hdr++;
                    if (f.Values.Any(v => !ColorTransform.CanTransform(v))) Negative++;
                    if (!f.Recolourable) NotRecolourable++;
                    Prob[f.Probability] = Prob.GetValueOrDefault(f.Probability) + 1;
                    if (f.Probability != EffectProbability.None) ProbByField[f.Key.Field + ":" + f.Probability] = ProbByField.GetValueOrDefault(f.Key.Field + ":" + f.Probability) + 1;
                    CountSegments(f);
                }
                var factors = fs.Where(f => f.Key.IsProductFactor).ToList();
                if (factors.Count == 0) EmittersNoValueFactor++;
                if (factors.Count >= 2) EmittersWithTwoFactors++;
                if (factors.Count(f => f.Chroma > 0.1f) >= 2) EmittersWithTwoColoured++;
                if (factors.Count(f => f.Chroma > 0.1f) >= 2 && factors.Where(f => f.Key.Role == EffectColorRole.Birth).Any(f => f.Chroma > 0.1f)
                    && factors.Where(f => f.Key.Role != EffectColorRole.Birth).Any(f => f.Chroma > 0.1f)) ColouredCarrierOther++;
                if (factors.Count > 0 && factors.All(f => f.Chroma <= 0.1f)) EmittersOnlyAlphaCurves++;
                ProductHue(fs);
            }
        }

        /// <summary>For an emitter whose birth colour AND colour over life both carry colour: the hue of the product of the two TRANSFORMED factors (each by the same
        /// hue shift) against the hue the product would have had it been shifted as one colour. How far hue-on-every-factor is from hue-on-the-product.</summary>
        private void ProductHue(List<EffectColorField> fs)
        {
            var birth = fs.FirstOrDefault(f => f.Key.Role == EffectColorRole.Birth);
            var life = fs.FirstOrDefault(f => f.Key.Role == EffectColorRole.OverLife);
            if (birth is null || life is null || birth.Chroma <= 0.1f || life.Chroma <= 0.1f) return;
            var b0 = birth.Values.First();
            if (!ColorTransform.CanTransform(b0)) return;
            foreach (int deg in new[] { 60, 120, 180 })
            {
                var t = new ColorTransform { HueShiftDegrees = deg };
                foreach (var l in life.Values)
                {
                    if (!ColorTransform.CanTransform(l)) continue;
                    var p = new Vector4(b0.X * l.X, b0.Y * l.Y, b0.Z * l.Z, 1f);
                    if (Math.Max(p.X, Math.Max(p.Y, p.Z)) < 0.02f || SkinEffectColors.Chroma(p) < 0.1f) continue;
                    var viaFactors = t.Apply(b0) * t.Apply(l);
                    var viaProduct = t.Apply(p);
                    float err = HueDistance(viaFactors, viaProduct);
                    ProdSamples[deg] = ProdSamples.GetValueOrDefault(deg) + 1;
                    ProdErrSum[deg] = ProdErrSum.GetValueOrDefault(deg) + err;
                    if (err > 15f) ProdOver15[deg] = ProdOver15.GetValueOrDefault(deg) + 1;
                    if (err > 30f) ProdOver30[deg] = ProdOver30.GetValueOrDefault(deg) + 1;
                }
            }
        }

        public readonly Dictionary<int, int> ProdSamples = new(), ProdOver15 = new(), ProdOver30 = new();
        public readonly Dictionary<int, double> ProdErrSum = new();

        private static float HueDistance(Vector4 a, Vector4 b)
        {
            static float Hue(Vector4 c)
            {
                float m = Math.Max(c.X, Math.Max(c.Y, c.Z));
                float r = c.X, g = c.Y, bl = c.Z;
                if (m > 1f) { r /= m; g /= m; bl /= m; }
                return ReyEngine.Core.Decoding.Hsv.FromRgb(r, g, bl, out float h, out _, out _) ? h * 360f : float.NaN;
            }
            float ha = Hue(a), hb = Hue(b);
            if (float.IsNaN(ha) || float.IsNaN(hb)) return 0f;
            float d = Math.Abs(ha - hb);
            return Math.Min(d, 360f - d);
        }

        private void CountSegments(EffectColorField f)
        {
            if (f.Keys.Count < 2) return;
            foreach (int deg in new[] { 60, 120, 180 })
            {
                var t = new ColorTransform { HueShiftDegrees = deg };
                for (int i = 0; i + 1 < f.Keys.Count; i++)
                {
                    var a = f.Keys[i]; var b = f.Keys[i + 1];
                    if (!ColorTransform.CanTransform(a) || !ColorTransform.CanTransform(b)) continue;
                    var mid = Vector4.Lerp(a, b, 0.5f);
                    var viaKeys = Vector4.Lerp(t.Apply(a), t.Apply(b), 0.5f);
                    var viaMid = t.Apply(mid);
                    float m = Math.Max(1f, Math.Max(Math.Max(a.X, a.Y), Math.Max(a.Z, Math.Max(b.X, Math.Max(b.Y, b.Z)))));   // relative to the HDR scale
                    float dev = Math.Max(Math.Abs(viaKeys.X - viaMid.X), Math.Max(Math.Abs(viaKeys.Y - viaMid.Y), Math.Abs(viaKeys.Z - viaMid.Z))) / m;
                    if (deg == 60) { this.Seg++; if (Chroma(a) > 0.1f && Chroma(b) > 0.1f) SegmentsBothColoured++; }
                    if (dev > 0.1f && (Chroma(a) > 0.1f || Chroma(b) > 0.1f)) Bent[deg] = Bent.GetValueOrDefault(deg) + 1;
                    if (dev > 0.25f && (Chroma(a) > 0.1f || Chroma(b) > 0.1f)) BentTight[deg] = BentTight.GetValueOrDefault(deg) + 1;
                }
            }
        }

        public int Seg;
        private static float Chroma(Vector4 v) => SkinEffectColors.Chroma(v);

        public void Merge(Tally o)
        {
            Systems += o.Systems; Emitters += o.Emitters; Fields += o.Fields; Constant += o.Constant; Curve += o.Curve; KeysTotal += o.KeysTotal;
            Hdr += o.Hdr; Negative += o.Negative; NotRecolourable += o.NotRecolourable;
            EmittersWithTwoFactors += o.EmittersWithTwoFactors; EmittersWithTwoColoured += o.EmittersWithTwoColoured;
            EmittersNoValueFactor += o.EmittersNoValueFactor; EmittersOnlyAlphaCurves += o.EmittersOnlyAlphaCurves; ColouredCarrierOther += o.ColouredCarrierOther;
            Seg += o.Seg; SegmentsBothColoured += o.SegmentsBothColoured;
            foreach (var (k, v) in o.ByField) ByField[k] = ByField.GetValueOrDefault(k) + v;
            foreach (var (k, v) in o.Prob) Prob[k] = Prob.GetValueOrDefault(k) + v;
            foreach (var (k, v) in o.ProbByField) ProbByField[k] = ProbByField.GetValueOrDefault(k) + v;
            foreach (var (k, v) in o.ProdSamples) ProdSamples[k] = ProdSamples.GetValueOrDefault(k) + v;
            foreach (var (k, v) in o.ProdOver15) ProdOver15[k] = ProdOver15.GetValueOrDefault(k) + v;
            foreach (var (k, v) in o.ProdOver30) ProdOver30[k] = ProdOver30.GetValueOrDefault(k) + v;
            foreach (var (k, v) in o.ProdErrSum) ProdErrSum[k] = ProdErrSum.GetValueOrDefault(k) + v;
            foreach (var (k, v) in o.Bent) Bent[k] = Bent.GetValueOrDefault(k) + v;
            foreach (var (k, v) in o.BentTight) BentTight[k] = BentTight.GetValueOrDefault(k) + v;
        }

        public string Report()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"  distinct systems reached {Systems}, emitters with a colour value {Emitters}, colour fields {Fields} (constant {Constant}, curve {Curve} with {KeysTotal} keys), HDR {Hdr}, negative {Negative}, not recolourable {NotRecolourable}");
            sb.AppendLine("  by field: " + string.Join(", ", ByField.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
            sb.AppendLine("  probability tables: " + string.Join(", ", Prob.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
            sb.AppendLine("    by field: " + string.Join(", ", ProbByField.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
            sb.AppendLine($"  emitters: with >=2 value factors {EmittersWithTwoFactors}; with >=2 COLOURED factors (saturation > 0.1) {EmittersWithTwoColoured}; birth AND life both coloured {ColouredCarrierOther}; every value factor neutral {EmittersOnlyAlphaCurves}; no product-factor value at all {EmittersNoValueFactor}");
            sb.AppendLine($"  curve segments (adjacent keys) {Seg}, both ends coloured {SegmentsBothColoured}");
            foreach (int deg in new[] { 60, 120, 180 })
                sb.AppendLine($"  hue +{deg}: segments whose midpoint colour differs by > 0.1 {Bent.GetValueOrDefault(deg)}, by > 0.25 {BentTight.GetValueOrDefault(deg)}");
            foreach (int deg in new[] { 60, 120, 180 })
                sb.AppendLine($"  product of two coloured factors, hue +{deg} on each: samples {ProdSamples.GetValueOrDefault(deg)}, mean hue error vs shifting the product "
                              + $"{(ProdSamples.GetValueOrDefault(deg) == 0 ? 0 : ProdErrSum.GetValueOrDefault(deg) / ProdSamples[deg]):F1} deg, over 15 deg {ProdOver15.GetValueOrDefault(deg)}, over 30 deg {ProdOver30.GetValueOrDefault(deg)}");
            return sb.ToString();
        }
    }
}
