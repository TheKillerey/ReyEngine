using System.Globalization;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Materials;

/// <summary>
/// M646: one parameter a StaticMaterialDef's <c>dynamicMaterial</c> (a DynamicMaterialDef) drives at
/// runtime.
///
/// <para>The entry in <c>paramValues</c> is what the material editor shows; it is not what the game draws.
/// Locke's five Onsen materials author <c>VCDissolve_Value = 1.23</c> - which dissolves everything below
/// a quarter of his height - and drive it from an IsDead lerp: -0.8 while alive, so nothing dissolves,
/// and 3 over eight seconds once dead, which is the death dissolve. A preview that draws the authored
/// value draws him dead. <c>Transition_Value</c> is the same story with his W buff.</para>
///
/// <para>Only what the bin WRITES is evaluated. A lerp whose rest branch is unwritten runs on the class
/// default, which this reader does not know and does not guess: <see cref="RestValue"/> is null, the
/// authored value stands, and <see cref="RestReason"/> says so.</para>
/// </summary>
public sealed class MaterialDynamicParameter
{
    public string Name { get; }
    /// <summary><c>Enabled</c> on the entry (absent = true). A disabled entry is dead data.</summary>
    public bool Enabled { get; }
    /// <summary>The driver tree on one line, for reports and tooltips.</summary>
    public string Driver { get; }
    /// <summary>What the driver yields at rest - idle, alive, unbuffed - when the bin writes it.</summary>
    public Vector4? RestValue { get; }
    /// <summary>Why <see cref="RestValue"/> is what it is, or why it is null.</summary>
    public string RestReason { get; }
    /// <summary>The other branch of a lerp driver, when written: the value the parameter animates to.</summary>
    public Vector4? ActiveValue { get; }

    internal MaterialDynamicParameter(string name, bool enabled, string driver, Vector4? rest, string reason, Vector4? active)
    {
        Name = name; Enabled = enabled; Driver = driver; RestValue = rest; RestReason = reason; ActiveValue = active;
    }

    /// <summary>One line for a report: name, rest value, driver.</summary>
    public string Summary => RestValue is { } r
        ? $"{Name} = {MaterialDrivers.Fmt(r)} at rest ({RestReason}); {Driver}"
        : $"{Name}: driven by {Driver}; {RestReason} - the authored value stands";
}

/// <summary>M646: reading a DynamicMaterialDef and evaluating its drivers at rest. See
/// <see cref="MaterialDynamicParameter"/>.</summary>
public static class MaterialDrivers
{
    /// <summary>Read <c>dynamicMaterial</c> off a StaticMaterialDef. Empty when absent.</summary>
    public static IReadOnlyList<MaterialDynamicParameter> Parse(BinTreeProperty? dynamicMaterial, Func<uint, string?> resolve)
    {
        if (dynamicMaterial is not BinTreeStruct def) return Array.Empty<MaterialDynamicParameter>();
        if (Field(def.Properties, "parameters") is not BinTreeContainer list) return Array.Empty<MaterialDynamicParameter>();
        var result = new List<MaterialDynamicParameter>();
        foreach (var el in list.Elements)
        {
            if (el is not BinTreeStruct entry) continue;
            string? name = (Field(entry.Properties, "name") as BinTreeString)?.Value;
            if (string.IsNullOrEmpty(name)) continue;
            bool enabled = Field(entry.Properties, "Enabled") switch
            {
                BinTreeBool b => b.Value,
                BinTreeBitBool bb => bb.Value,
                _ => true,
            };
            if (Field(entry.Properties, "driver") is not BinTreeStruct driver)
            {
                result.Add(new MaterialDynamicParameter(name, enabled, "(no driver)", null, "no driver is written", null));
                continue;
            }
            var (rest, active, reason) = ValueAtRest(driver, resolve);
            result.Add(new MaterialDynamicParameter(name, enabled, Describe(driver, resolve), rest, reason, active));
        }
        return result;
    }

    /// <summary>
    /// Bool drivers that are false at rest - alive, unbuffed, standing still, not casting or attacking,
    /// no animation event received, nothing triggered. Every other bool driver is "unknown" here and keeps
    /// the authored value: IsAnimationPlaying depends on the clip, LearnedSpell on the level, HasGear,
    /// IsLocalPlayer, IsEnemy and the map-prop / fx-object queries on things a preview has no answer for.
    /// </summary>
    private static readonly HashSet<string> RestFalse = new(StringComparer.Ordinal)
    {
        "IsDeadDynamicMaterialBoolDriver",
        "HasBuffDynamicMaterialBoolDriver", "HasBuffOfTypeBoolDriver", "HasBuffWithAttributeBoolDriver",
        "IsMovingBoolDriver", "IsAttackingBoolDriver", "IsCastingBoolDriver",
        "HasLossOfControlTypeBoolDriver",
        "HasReceivedAnimEmoteEventBoolDriver", "HasReceivedAnimationEventBoolDriver",
        "IsInGrassDynamicMaterialBoolDriver",
        "FixedDurationTriggeredBoolDriver",
    };

    /// <summary>Whether a bool driver is true at rest. Null when it is not one this reader evaluates.</summary>
    internal static bool? BoolAtRest(BinTreeStruct d, Func<uint, string?> resolve)
    {
        string cls = ClassName(d, resolve);
        switch (cls)
        {
            case "NotMaterialDriver":
                return Field(d.Properties, "mDriver") is BinTreeStruct inner && BoolAtRest(inner, resolve) is { } v ? !v : null;
            case "AllTrueMaterialDriver":
            case "OneTrueMaterialDriver":
            {
                if (Field(d.Properties, "mDrivers") is not BinTreeContainer c) return null;
                bool all = cls == "AllTrueMaterialDriver";
                bool acc = all;
                foreach (var e in c.Elements)
                {
                    if (e is not BinTreeStruct s || BoolAtRest(s, resolve) is not { } x) return null;
                    acc = all ? acc && x : acc || x;
                }
                return acc;
            }
        }
        return RestFalse.Contains(cls) ? false : null;
    }

    /// <summary>The value a float or colour driver yields at rest, the value it animates to when that is a
    /// written lerp branch, and the reason - which is also the reason when the rest value is null.</summary>
    internal static (Vector4? AtRest, Vector4? Active, string Reason) ValueAtRest(BinTreeStruct d, Func<uint, string?> resolve)
    {
        string cls = ClassName(d, resolve);
        switch (cls)
        {
            case "LerpMaterialDriver":
            {
                if (Field(d.Properties, "mBoolDriver") is not BinTreeStruct b) return (null, null, "lerp without a bool driver");
                string gate = Short(ClassName(b, resolve));
                bool? state = BoolAtRest(b, resolve);
                if (state is null) return (null, null, $"{gate}: rest state unknown");
                float? on = F32(Field(d.Properties, "mOnValue")), off = F32(Field(d.Properties, "mOffValue"));
                float? rest = state.Value ? on : off, active = state.Value ? off : on;
                string branch = state.Value ? "mOnValue" : "mOffValue";
                string truth = state.Value ? "true" : "false";
                if (rest is null) return (null, V(active), $"{gate} is {truth} at rest and {branch} is not written");
                return (V(rest), V(active), $"{gate} is {truth} at rest -> {branch}");
            }
            case "FloatLiteralMaterialDriver":
                return F32(Field(d.Properties, "mValue")) is { } lit ? (V(lit), null, "literal") : (null, null, "literal without a written value");
            case "ColorLiteralMaterialDriver":
                return Color(Field(d.Properties, "mValue") ?? Field(d.Properties, "mColor")) is { } col ? (col, null, "literal") : (null, null, "literal without a written value");
            case "ColorGraphMaterialDriver":
            {
                if (Field(d.Properties, "driver") is not BinTreeStruct inner) return (null, null, "colour graph without a driver");
                var (t, _, why) = ValueAtRest(inner, resolve);
                if (t is null) return (null, null, "colour graph: " + why);
                if (Field(d.Properties, "colors") is not BinTreeStruct graph) return (null, null, "colour graph without keys");
                var times = (Field(graph.Properties, "times") as BinTreeContainer)?.Elements.Select(e => F32(e)).ToList();
                var values = (Field(graph.Properties, "values") as BinTreeContainer)?.Elements.Select(Color).ToList();
                if (values is not { Count: > 0 } || times is null || times.Count != values.Count || times.Any(x => x is null) || values.Any(x => x is null))
                    return (null, null, "colour graph keys unreadable");
                return (Sample(times.Select(x => x!.Value).ToList(), values.Select(x => x!.Value).ToList(), t.Value.X), null,
                    $"colour graph at t={Fmt(t.Value.X)} ({why})");
            }
        }
        return (null, null, $"{Short(cls)}: not evaluated");
    }

    private static Vector4 Sample(List<float> times, List<Vector4> values, float t)
    {
        if (t <= times[0]) return values[0];
        for (int i = 1; i < times.Count; i++)
            if (t <= times[i])
            {
                float span = times[i] - times[i - 1];
                float u = span <= 0f ? 1f : (t - times[i - 1]) / span;
                return Vector4.Lerp(values[i - 1], values[i], u);
            }
        return values[^1];
    }

    /// <summary>The driver tree on one line: <c>Lerp(IsDead: on 3, off -0.8)</c>.</summary>
    internal static string Describe(BinTreeStruct d, Func<uint, string?> resolve)
    {
        string cls = ClassName(d, resolve);
        string name = Short(cls);
        var bits = new List<string>();
        switch (cls)
        {
            case "LerpMaterialDriver":
                if (Field(d.Properties, "mBoolDriver") is BinTreeStruct b) bits.Add(Describe(b, resolve));
                if (F32(Field(d.Properties, "mOnValue")) is { } on) bits.Add("on " + Fmt(on));
                if (F32(Field(d.Properties, "mOffValue")) is { } off) bits.Add("off " + Fmt(off));
                break;
            case "NotMaterialDriver":
                if (Field(d.Properties, "mDriver") is BinTreeStruct inner) bits.Add(Describe(inner, resolve));
                break;
            case "AllTrueMaterialDriver":
            case "OneTrueMaterialDriver":
                if (Field(d.Properties, "mDrivers") is BinTreeContainer c)
                    bits.AddRange(c.Elements.OfType<BinTreeStruct>().Select(s => Describe(s, resolve)));
                break;
            case "ColorGraphMaterialDriver":
                if (Field(d.Properties, "driver") is BinTreeStruct g) bits.Add(Describe(g, resolve));
                break;
            case "HasBuffDynamicMaterialBoolDriver":
                if (Field(d.Properties, "mScriptName") is BinTreeString sn) bits.Add(sn.Value);
                else if (Field(d.Properties, "Spell") is BinTreeHash sh) bits.Add(resolve(sh.Value)?.Split('/').Last() ?? $"0x{sh.Value:x8}");
                break;
            case "FloatLiteralMaterialDriver":
                if (F32(Field(d.Properties, "mValue")) is { } lit) bits.Add(Fmt(lit));
                break;
        }
        return bits.Count == 0 ? name : $"{name}({string.Join(", ", bits)})";
    }

    private static string ClassName(BinTreeStruct s, Func<uint, string?> resolve) => resolve(s.ClassHash) ?? $"0x{s.ClassHash:x8}";

    /// <summary>IsDeadDynamicMaterialBoolDriver -> IsDead, LerpMaterialDriver -> Lerp.</summary>
    internal static string Short(string cls)
    {
        string s = cls.Replace("DynamicMaterial", "", StringComparison.Ordinal).Replace("Material", "", StringComparison.Ordinal);
        if (s.EndsWith("Driver", StringComparison.Ordinal)) s = s[..^6];
        if (s.EndsWith("Bool", StringComparison.Ordinal) || s.EndsWith("Float", StringComparison.Ordinal))
            s = s.EndsWith("Bool", StringComparison.Ordinal) ? s[..^4] : s[..^5];
        return s.Length == 0 ? cls : s;
    }

    private static float? F32(BinTreeProperty? p) => p is BinTreeF32 f ? f.Value : null;
    private static Vector4? V(float? f) => f is { } x ? new Vector4(x, 0f, 0f, 0f) : null;
    private static Vector4? Color(BinTreeProperty? p) => p switch
    {
        BinTreeColor c => c.Value,
        BinTreeVector4 v => v.Value,
        BinTreeVector3 v3 => new Vector4(v3.Value, 1f),
        _ => null,
    };

    public static string Fmt(float x) => x.ToString("0.###", CultureInfo.InvariantCulture);
    public static string Fmt(Vector4 v) => v.Y == 0f && v.Z == 0f && v.W == 0f
        ? Fmt(v.X)
        : $"({Fmt(v.X)}, {Fmt(v.Y)}, {Fmt(v.Z)}, {Fmt(v.W)})";

    private static BinTreeProperty? Field(IReadOnlyDictionary<uint, BinTreeProperty> props, string name)
    {
        if (props.TryGetValue(HashAlgorithms.Fnv1aRaw(name), out var p)) return p;
        if (props.TryGetValue(HashAlgorithms.Fnv1a(name), out p)) return p;
        return null;
    }
}
