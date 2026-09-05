using System.Globalization;
using System.Numerics;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.Materials;

/// <summary>
/// M647: one condition a skin's material drivers ask about - "is he dead", "does he have LockeW",
/// "is the taunt playing". The preview cannot know the answer, so it does not assume one: every
/// condition is off until something turns it on, and <see cref="MaterialDriverState"/> is what turns it on.
/// </summary>
/// <param name="Key">Stable identity, e.g. <c>IsDead</c> or <c>HasBuff:LockeW</c>. Two materials asking
/// the same question share a key, which is what makes one switch drive the whole skin.</param>
/// <param name="Kind">The bool driver's short class name - <c>IsDead</c>, <c>HasBuff</c>, …</param>
/// <param name="Label">What to call it in a UI: the buff or animation name, else the kind.</param>
public readonly record struct MaterialDriverCondition(string Key, string Kind, string Label)
{
    /// <summary>A sentence for a tooltip: what has to be true in game for this to be on.</summary>
    public string Describe => Kind switch
    {
        "IsDead" => "the champion is dead",
        "HasBuff" => $"the buff '{Label}' is active",
        "IsAnimationPlaying" => $"the animation '{Label}' is playing",
        "IsMoving" => "the champion is moving",
        "IsAttacking" => "the champion is attacking",
        "IsCasting" => "the champion is casting",
        "IsInGrass" => "the champion is in grass",
        "IsEnemy" => "the champion is an enemy",
        "LearnedSpell" => $"the spell '{Label}' is learned",
        "HasGear" => $"the gear '{Label}' is equipped",
        _ => Label,
    };
}

/// <summary>
/// M647: the situation the preview is asserting - which conditions hold, and how long they have held.
///
/// <para>The seconds matter because Riot's drivers are lerps with their own durations: Locke's death
/// dissolve runs over 8 s and Aatrox's over 4 s, so "dead" is not one picture but a ramp. Held at its end
/// the dissolve is complete and the champion is gone (measured: Locke drops from 108,825 covered pixels
/// to 39,689), which is correct and useless to look at - so the state carries a position along the ramp
/// rather than only its endpoint.</para>
/// </summary>
public sealed class MaterialDriverState
{
    /// <summary>Nothing asserted: alive, unbuffed, standing still. What a freshly loaded skin draws as.</summary>
    public static readonly MaterialDriverState Rest = new(Array.Empty<string>(), 0f);

    private readonly HashSet<string> _active;

    public MaterialDriverState(IEnumerable<string> activeKeys, float seconds)
    {
        _active = new HashSet<string>(activeKeys, StringComparer.OrdinalIgnoreCase);
        Seconds = MathF.Max(0f, seconds);
    }

    /// <summary>How long the active conditions have held, in seconds. A driver's own
    /// <c>mTurnOnTimeSec</c> turns this into its position along the transition.</summary>
    public float Seconds { get; }

    public IReadOnlyCollection<string> ActiveKeys => _active;
    public bool IsActive(string key) => _active.Contains(key);
    public bool IsRest => _active.Count == 0;

    public MaterialDriverState With(string key, bool on)
    {
        var next = new HashSet<string>(_active, StringComparer.OrdinalIgnoreCase);
        if (on) next.Add(key); else next.Remove(key);
        return new MaterialDriverState(next, Seconds);
    }

    public MaterialDriverState WithSeconds(float seconds) => new(_active, seconds);

    /// <summary>How far along a transition of <paramref name="duration"/> seconds this state is, 0..1.
    /// A duration of zero (or less) is instant.</summary>
    public float Progress(float duration) => duration <= 0f ? 1f : Math.Clamp(Seconds / duration, 0f, 1f);

    public override string ToString() => _active.Count == 0 ? "rest" : string.Join(" + ", _active) + $" @ {Seconds:0.##}s";
}

/// <summary>The outcome of evaluating one driver: a value, or the reason there is none.</summary>
/// <param name="Value">Null when this reader will not answer - the authored value then stands.</param>
/// <param name="Reason">Why the value is what it is, or why there is none. Always set.</param>
public readonly record struct MaterialDriverValue(Vector4? Value, string Reason);

/// <summary>
/// M646/M647: one parameter a StaticMaterialDef's <c>dynamicMaterial</c> (a DynamicMaterialDef) drives.
///
/// <para>The entry in <c>paramValues</c> is what the material editor shows; it is not what the game draws.
/// Locke's five Onsen materials author <c>VCDissolve_Value = 1.23</c> - which dissolves everything below
/// a quarter of his height - and drive it from an IsDead lerp: -0.8 while alive, 3 once dead. A preview
/// that draws the authored value draws him dead. <c>Transition_Value</c> is the same story with his W.</para>
///
/// <para>The driver tree is kept, not just its value, so the same parameter can be asked what it reads in
/// any <see cref="MaterialDriverState"/> - which is what the character editor's state switch does.</para>
/// </summary>
public sealed class MaterialDynamicParameter
{
    private readonly BinTreeStruct? _driver;
    private readonly Func<uint, string?> _resolve;

    internal MaterialDynamicParameter(string name, bool enabled, BinTreeStruct? driver, Func<uint, string?> resolve)
    {
        Name = name; Enabled = enabled; _driver = driver; _resolve = resolve;
        Driver = driver is null ? "(no driver)" : MaterialDrivers.Describe(driver, resolve);
        Conditions = driver is null
            ? Array.Empty<MaterialDriverCondition>()
            : MaterialDrivers.ConditionsOf(driver, resolve).ToArray();
        TransitionSeconds = driver is null ? 0f : MaterialDrivers.LongestTransition(driver, resolve);
    }

    public string Name { get; }

    /// <summary><c>Enabled</c> on the entry (absent = true). A disabled entry is dead data.</summary>
    public bool Enabled { get; }

    /// <summary>The driver tree on one line, for reports and tooltips.</summary>
    public string Driver { get; }

    /// <summary>Every condition this parameter's driver asks about.</summary>
    public IReadOnlyList<MaterialDriverCondition> Conditions { get; }

    /// <summary>The longest <c>mTurnOnTimeSec</c> anywhere in this driver - how long the game takes to
    /// reach the value once its condition turns on.</summary>
    public float TransitionSeconds { get; }

    /// <summary>What this parameter reads in the given situation.</summary>
    public MaterialDriverValue Evaluate(MaterialDriverState state) =>
        _driver is null
            ? new MaterialDriverValue(null, "no driver is written")
            : MaterialDrivers.ValueIn(_driver, state, _resolve);

    /// <summary>What it reads at rest - alive, unbuffed, idle.</summary>
    public Vector4? RestValue => Evaluate(MaterialDriverState.Rest).Value;
    public string RestReason => Evaluate(MaterialDriverState.Rest).Reason;

    /// <summary>What it reads with every one of its own conditions on and the transition complete - the
    /// other end of the ramp.</summary>
    public Vector4? ActiveValue =>
        Conditions.Count == 0 ? null
        : Evaluate(new MaterialDriverState(Conditions.Select(c => c.Key), float.MaxValue)).Value;

    /// <summary>One line for a report: name, rest value, driver.</summary>
    public string Summary => RestValue is { } r
        ? $"{Name} = {MaterialDrivers.Fmt(r)} at rest ({RestReason}); {Driver}"
        : $"{Name}: driven by {Driver}; {RestReason} - the authored value stands";
}

/// <summary>
/// M646/M647: reading a DynamicMaterialDef and evaluating its drivers in a named situation.
///
/// <para>Every class default below is read off Riot's own schema (<c>data/meta/meta.db.json</c>, built
/// from CommunityDragon's bintypes/binfields) rather than assumed - <c>DriverSchemaDefaultsTests</c> pins
/// each one against that file, so a schema change breaks a test instead of quietly changing a preview.
/// Riot writes these fields inconsistently: of 346 driven entries across every champion wad, 72 leave a
/// lerp branch unwritten, and reading such a branch as "unknown" left most of the corpus unevaluated.</para>
/// </summary>
public static class MaterialDrivers
{
    // ---- class defaults, from data/meta/meta.db.json (see DriverSchemaDefaultsTests) -----------------
    public const float LerpOnDefault = 1f;          // LerpMaterialDriver.mOnValue          0xb4c4e463
    public const float LerpOffDefault = 0f;         // LerpMaterialDriver.mOffValue
    public const float TurnTimeDefault = 1f;        // LerpMaterialDriver.mTurnOn/OffTimeSec
    public const float LiteralDefault = 0f;         // FloatLiteralMaterialDriver.mValue    0x70fff2b7
    public const float RemapInMaxDefault = 1f;      // RemapFloatMaterialDriver.mMaxValue   0xa9f7c49f
    public const float RemapOutMaxDefault = 1f;     // RemapFloatMaterialDriver.mOutputMaxValue
    public const float KeyFrameDefault = 0f;        // KeyFrameFloatClipReaderDriver.DefaultFloat 0xcc8cc1ee
    public static readonly Vector4 ChooserOnDefault = new(1f, 0f, 0f, 1f);    // ColorChooserMaterialDriver.mColorOn  0x4aff6f5a
    public static readonly Vector4 ChooserOffDefault = new(0f, 0f, 1f, 1f);   // ColorChooserMaterialDriver.mColorOff
    public static readonly Vector4 SpecificColorDefault = new(1f, 0f, 0f, 1f);// SpecificColorMaterialDriver.mColor   0x466b06ef
    public static readonly Vector4 Float4LiteralDefault = new(1f, 0f, 0f, 0f); // Float4LiteralMaterialDriver.value    0xf4c0192d
    public static readonly Vector4 Vec4OnDefault = new(1f, 1f, 1f, 1f);       // LerpVec4LogicDriver.OnValue          0x22d8a036
    public static readonly Vector4 Vec4OffDefault = new(0f, 0f, 0f, 1f);      // LerpVec4LogicDriver.OffValue

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
            result.Add(new MaterialDynamicParameter(name, enabled, Field(entry.Properties, "driver") as BinTreeStruct, resolve));
        }
        return result;
    }

    /// <summary>Every condition the given materials ask about, deduplicated - the switches a state
    /// control should offer for this skin. Disabled entries are left out: they are dead data.</summary>
    public static IReadOnlyList<MaterialDriverCondition> ConditionsOf(IEnumerable<MaterialBinding> bindings)
    {
        var seen = new Dictionary<string, MaterialDriverCondition>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in bindings)
            foreach (var p in b.DynamicParameters)
                if (p.Enabled)
                    foreach (var c in p.Conditions)
                        seen.TryAdd(c.Key, c);
        // deaths first, then buffs, then the rest - the order a switcher reads best in
        return seen.Values
            .OrderBy(c => c.Kind == "IsDead" ? 0 : c.Kind == "HasBuff" ? 1 : 2)
            .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ===================================================== bool drivers

    /// <summary>Whether a bool driver holds in this state. Null when this reader will not answer.
    ///
    /// <para>A leaf condition is simply "is its key active" - the preview asserts nothing about the game
    /// it cannot see, and the user turns the conditions on. Composites fold their children.</para></summary>
    internal static bool? BoolIn(BinTreeStruct d, MaterialDriverState state, Func<uint, string?> resolve)
    {
        string cls = ClassName(d, resolve);
        switch (cls)
        {
            case "NotMaterialDriver":
                return Field(d.Properties, "mDriver") is BinTreeStruct inner && BoolIn(inner, state, resolve) is { } v ? !v : null;
            case "DelayedBoolMaterialDriver":
                // a delay changes WHEN it flips, not what it settles on
                return Field(d.Properties, "mBoolDriver") is BinTreeStruct delayed ? BoolIn(delayed, state, resolve) : null;
            case "AllTrueMaterialDriver":
            case "OneTrueMaterialDriver":
            {
                if (Field(d.Properties, "mDrivers") is not BinTreeContainer c) return null;
                bool all = cls == "AllTrueMaterialDriver";
                bool acc = all;
                foreach (var e in c.Elements)
                {
                    if (e is not BinTreeStruct s || BoolIn(s, state, resolve) is not { } x) return null;
                    acc = all ? acc && x : acc || x;
                }
                return acc;
            }
            case "FloatComparisonMaterialDriver":
                return null;   // compares two float drivers; not a condition a switch can stand in for
        }
        return ConditionOf(d, resolve) is { } cond ? state.IsActive(cond.Key) : null;
    }

    /// <summary>The condition a leaf bool driver represents, or null when it is a composite, a value
    /// driver, or a class this reader cannot name.
    ///
    /// <para>A leaf is recognised by its class name ending in <c>BoolDriver</c>. That is not a guess: of
    /// the 51 classes deriving from IDynamicMaterialBoolDriver in Riot's schema, every named one ends in
    /// <c>BoolDriver</c> except the five composites handled above and RawBoolConceptLogicDriver, and the
    /// only other class with the suffix is the ILogicBoolDriver interface. The 13 hash-only ones cannot be
    /// named here anyway, and none of them appears in the champion corpus. Without this test a
    /// LerpMaterialDriver - which also ends in "Driver" - became a switch of its own.</para></summary>
    internal static MaterialDriverCondition? ConditionOf(BinTreeStruct d, Func<uint, string?> resolve)
    {
        string cls = ClassName(d, resolve);
        if (cls is "NotMaterialDriver" or "AllTrueMaterialDriver" or "OneTrueMaterialDriver"
            or "DelayedBoolMaterialDriver" or "FloatComparisonMaterialDriver") return null;
        if (!cls.EndsWith("BoolDriver", StringComparison.Ordinal) && cls != "RawBoolConceptLogicDriver") return null;
        string kind = Short(cls);
        string? detail = (Field(d.Properties, "mScriptName") as BinTreeString)?.Value;
        if (string.IsNullOrEmpty(detail) && Field(d.Properties, "Spell") is BinTreeHash spell && spell.Value != 0)
            detail = resolve(spell.Value)?.Split('/').Last() ?? $"0x{spell.Value:x8}";
        if (string.IsNullOrEmpty(detail) && Field(d.Properties, "mAnimationNames") is BinTreeContainer anims)
            detail = string.Join("/", anims.Elements.OfType<BinTreeHash>()
                .Select(h => resolve(h.Value)?.Split('/').Last() ?? $"0x{h.Value:x8}"));
        string key = string.IsNullOrEmpty(detail) ? kind : kind + ":" + detail;
        return new MaterialDriverCondition(key, kind, string.IsNullOrEmpty(detail) ? Friendly(kind) : detail!);
    }

    private static string Friendly(string kind) => kind switch
    {
        "IsDead" => "Dead",
        "IsMoving" => "Moving",
        "IsAttacking" => "Attacking",
        "IsCasting" => "Casting",
        "IsInGrass" => "In grass",
        "IsEnemy" => "Enemy",
        "IsLocalPlayer" => "Local player",
        _ => kind,
    };

    // ===================================================== value drivers

    /// <summary>What a float or colour driver yields in this state.</summary>
    internal static MaterialDriverValue ValueIn(BinTreeStruct d, MaterialDriverState state, Func<uint, string?> resolve)
    {
        string cls = ClassName(d, resolve);
        switch (cls)
        {
            case "LerpMaterialDriver":
                return LerpIn(d, state, resolve,
                    "mBoolDriver", "mOnValue", "mOffValue", "mTurnOnTimeSec",
                    V(LerpOnDefault), V(LerpOffDefault));
            case "LerpVec4LogicDriver":
                return LerpIn(d, state, resolve,
                    "BoolDriver", "OnValue", "OffValue", "TurnOnTimeSec",
                    Vec4OnDefault, Vec4OffDefault);

            case "FloatLiteralMaterialDriver":
                return new MaterialDriverValue(V(F32(Field(d.Properties, "mValue")) ?? LiteralDefault), "literal");
            case "Float4LiteralMaterialDriver":
                return new MaterialDriverValue(Colour(Field(d.Properties, "value")) ?? Float4LiteralDefault, "literal");
            case "SpecificColorMaterialDriver":
                return new MaterialDriverValue(Colour(Field(d.Properties, "mColor")) ?? SpecificColorDefault, "literal colour");

            case "ColorChooserMaterialDriver":
            {
                if (Field(d.Properties, "mBoolDriver") is not BinTreeStruct b) return new(null, "colour chooser without a condition");
                if (BoolIn(b, state, resolve) is not { } on) return new(null, $"{GateName(b, resolve)}: not evaluated");
                var value = on ? Colour(Field(d.Properties, "mColorOn")) ?? ChooserOnDefault
                               : Colour(Field(d.Properties, "mColorOff")) ?? ChooserOffDefault;
                return new(value, $"{GateName(b, resolve)} is {(on ? "on" : "off")} -> {(on ? "mColorOn" : "mColorOff")}");
            }

            case "MaxMaterialDriver":
            case "MinMaterialDriver":
            {
                if (Field(d.Properties, "mDrivers") is not BinTreeContainer c || c.Elements.Count == 0)
                    return new(null, Short(cls) + " has no drivers");
                bool max = cls == "MaxMaterialDriver";
                Vector4? acc = null;
                foreach (var e in c.Elements)
                {
                    if (e is not BinTreeStruct s) continue;
                    var got = ValueIn(s, state, resolve);
                    if (got.Value is not { } x) return new(null, Short(cls) + ": " + got.Reason);
                    acc = acc is not { } a ? x
                        : new Vector4(max ? MathF.Max(a.X, x.X) : MathF.Min(a.X, x.X),
                                      max ? MathF.Max(a.Y, x.Y) : MathF.Min(a.Y, x.Y),
                                      max ? MathF.Max(a.Z, x.Z) : MathF.Min(a.Z, x.Z),
                                      max ? MathF.Max(a.W, x.W) : MathF.Min(a.W, x.W));
                }
                return acc is { } r ? new(r, Short(cls) + " of " + c.Elements.Count) : new(null, Short(cls) + " has no drivers");
            }

            // BlendingSwitch only adds blend TIMES to the same selection, so it resolves identically
            case "SwitchMaterialDriver":
            case "BlendingSwitchMaterialDriver":
            {
                if (Field(d.Properties, "mElements") is BinTreeContainer els)
                    foreach (var e in els.Elements)
                    {
                        if (e is not BinTreeStruct el) continue;
                        if (Field(el.Properties, "mCondition") is not BinTreeStruct cond) continue;
                        if (BoolIn(cond, state, resolve) is not { } on) return new(null, $"switch: {GateName(cond, resolve)} not evaluated");
                        if (!on) continue;
                        if (Field(el.Properties, "mValue") is not BinTreeStruct val) return new(null, "switch element without a value");
                        var got = ValueIn(val, state, resolve);
                        return new(got.Value, $"switch on {GateName(cond, resolve)} -> {got.Reason}");
                    }
                if (Field(d.Properties, "mDefaultValue") is BinTreeStruct def)
                {
                    var got = ValueIn(def, state, resolve);
                    return new(got.Value, "switch default -> " + got.Reason);
                }
                return new(null, "switch: no element matches and no mDefaultValue is written");
            }

            case "ColorGraphMaterialDriver":
            case "FloatGraphMaterialDriver":
            {
                bool colour = cls == "ColorGraphMaterialDriver";
                if (Field(d.Properties, "driver") is not BinTreeStruct inner) return new(null, Short(cls) + " without a driver");
                var t = ValueIn(inner, state, resolve);
                if (t.Value is not { } at) return new(null, Short(cls) + ": " + t.Reason);
                if (Field(d.Properties, colour ? "colors" : "graph") is not BinTreeStruct graph) return new(null, Short(cls) + " without keys");
                var times = (Field(graph.Properties, "times") as BinTreeContainer)?.Elements.Select(F32).ToList();
                var values = (Field(graph.Properties, "values") as BinTreeContainer)?.Elements
                    .Select(e => colour ? Colour(e) : V(F32(e))).ToList();
                if (values is not { Count: > 0 } || times is null || times.Count != values.Count
                    || times.Any(x => x is null) || values.Any(x => x is null))
                    return new(null, Short(cls) + " keys unreadable");
                return new(Sample(times.Select(x => x!.Value).ToList(), values.Select(x => x!.Value).ToList(), at.X),
                    $"{Short(cls)} at {Fmt(at.X)} ({t.Reason})");
            }

            case "RemapFloatMaterialDriver":
            {
                if (Field(d.Properties, "mDriver") is not BinTreeStruct inner) return new(null, "remap without a driver");
                var got = ValueIn(inner, state, resolve);
                if (got.Value is not { } x) return new(null, "remap: " + got.Reason);
                float inMin = F32(Field(d.Properties, "mMinValue")) ?? 0f, inMax = F32(Field(d.Properties, "mMaxValue")) ?? RemapInMaxDefault;
                float outMin = F32(Field(d.Properties, "mOutputMinValue")) ?? 0f, outMax = F32(Field(d.Properties, "mOutputMaxValue")) ?? RemapOutMaxDefault;
                // Clamped to the output range - the usual convention for a named remap, but NOT verified
                // against the client. Only 8 entries in the whole corpus depend on it.
                float u = inMax - inMin == 0f ? 0f : Math.Clamp((x.X - inMin) / (inMax - inMin), 0f, 1f);
                return new(V(outMin + u * (outMax - outMin)), $"remap of ({got.Reason})");
            }

            case "KeyFrameFloatClipReaderDriver":
                // reads a float out of the playing animation clip; with no clip driving it that is its default
                return new(V(F32(Field(d.Properties, "DefaultFloat")) ?? KeyFrameDefault),
                    "no animation clip is driving it -> DefaultFloat");

            case "SineMaterialDriver":
            case "TimeMaterialDriver":
                return new(null, Short(cls) + " changes with time, not with a state");
        }
        // A bool driver written straight into a value slot (Evelynn's Empowered_E). The engine has to
        // coerce it, and 1/0 is the only coercion a bool admits.
        if (BoolIn(d, state, resolve) is { } asValue)
            return new(V(asValue ? 1f : 0f), $"{GateName(d, resolve)} is {(asValue ? "on" : "off")} -> a bool in a value slot reads 1/0");
        return new(null, $"{Short(cls)}: not evaluated");
    }

    /// <summary>The shared body of the two lerp drivers: gate off settles at the off value, gate on ramps
    /// from off to on across the driver's own turn-on time.</summary>
    private static MaterialDriverValue LerpIn(BinTreeStruct d, MaterialDriverState state, Func<uint, string?> resolve,
        string gateField, string onField, string offField, string timeField, Vector4 onDefault, Vector4 offDefault)
    {
        if (Field(d.Properties, gateField) is not BinTreeStruct b) return new(null, "lerp without a condition");
        string gate = GateName(b, resolve);
        if (BoolIn(b, state, resolve) is not { } on) return new(null, $"{gate}: not evaluated");
        Vector4 offValue = Value4(Field(d.Properties, offField)) ?? offDefault;
        Vector4 onValue = Value4(Field(d.Properties, onField)) ?? onDefault;
        if (!on) return new(offValue, $"{gate} is off -> {offField}");
        float duration = F32(Field(d.Properties, timeField)) ?? TurnTimeDefault;
        float u = state.Progress(duration);
        string how = u >= 1f ? $"{gate} is on -> {onField}"
            : $"{gate} is on, {Fmt(state.Seconds)}s of {Fmt(duration)}s -> {Fmt(u * 100f)}% toward {onField}";
        return new(Vector4.Lerp(offValue, onValue, u), how);
    }

    /// <summary>The longest turn-on time anywhere in a driver tree - how long the game takes to settle
    /// after its condition flips.</summary>
    internal static float LongestTransition(BinTreeStruct d, Func<uint, string?> resolve)
    {
        // A lerp that omits its turn-on time still takes the schema's second to settle, so report that
        // rather than zero - it is the range a "seconds since" slider has to cover.
        bool lerp = ClassName(d, resolve) is "LerpMaterialDriver" or "LerpVec4LogicDriver";
        float longest = F32(Field(d.Properties, "mTurnOnTimeSec")) ?? F32(Field(d.Properties, "TurnOnTimeSec"))
                        ?? (lerp ? TurnTimeDefault : 0f);
        foreach (var p in d.Properties.Values)
            foreach (var child in Structs(p))
                longest = MathF.Max(longest, LongestTransition(child, resolve));
        return longest;
    }

    /// <summary>Every condition named anywhere in a driver tree.</summary>
    internal static IEnumerable<MaterialDriverCondition> ConditionsOf(BinTreeStruct d, Func<uint, string?> resolve)
    {
        if (ConditionOf(d, resolve) is { } own) yield return own;
        foreach (var p in d.Properties.Values)
            foreach (var child in Structs(p))
                foreach (var c in ConditionsOf(child, resolve))
                    yield return c;
    }

    /// <summary>The struct children of a property, whether it holds one or a container of them.</summary>
    private static IEnumerable<BinTreeStruct> Structs(BinTreeProperty p)
    {
        switch (p)
        {
            case BinTreeStruct s: yield return s; break;
            case BinTreeContainer c:
                foreach (var e in c.Elements) if (e is BinTreeStruct es) yield return es;
                break;
        }
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
            case "LerpVec4LogicDriver":
            {
                bool vec = cls == "LerpVec4LogicDriver";
                if (Field(d.Properties, vec ? "BoolDriver" : "mBoolDriver") is BinTreeStruct b) bits.Add(Describe(b, resolve));
                if (Value4(Field(d.Properties, vec ? "OnValue" : "mOnValue")) is { } on) bits.Add("on " + Fmt(on));
                if (Value4(Field(d.Properties, vec ? "OffValue" : "mOffValue")) is { } off) bits.Add("off " + Fmt(off));
                break;
            }
            case "NotMaterialDriver":
                if (Field(d.Properties, "mDriver") is BinTreeStruct inv) bits.Add(Describe(inv, resolve));
                break;
            case "DelayedBoolMaterialDriver":
                if (Field(d.Properties, "mBoolDriver") is BinTreeStruct del) bits.Add(Describe(del, resolve));
                break;
            case "AllTrueMaterialDriver":
            case "OneTrueMaterialDriver":
            case "MaxMaterialDriver":
            case "MinMaterialDriver":
                if (Field(d.Properties, "mDrivers") is BinTreeContainer c)
                    bits.AddRange(c.Elements.OfType<BinTreeStruct>().Select(s => Describe(s, resolve)));
                break;
            case "SwitchMaterialDriver":
            case "BlendingSwitchMaterialDriver":
                if (Field(d.Properties, "mElements") is BinTreeContainer els)
                    bits.AddRange(els.Elements.OfType<BinTreeStruct>()
                        .Select(e => Field(e.Properties, "mCondition") is BinTreeStruct cd ? Describe(cd, resolve) : "?"));
                break;
            case "ColorChooserMaterialDriver":
                if (Field(d.Properties, "mBoolDriver") is BinTreeStruct ch) bits.Add(Describe(ch, resolve));
                break;
            case "ColorGraphMaterialDriver":
            case "FloatGraphMaterialDriver":
            case "RemapFloatMaterialDriver":
            case "InvertFloatMaterialDriver":
                if (Field(d.Properties, "driver") is BinTreeStruct g) bits.Add(Describe(g, resolve));
                else if (Field(d.Properties, "mDriver") is BinTreeStruct g2) bits.Add(Describe(g2, resolve));
                break;
            case "FloatLiteralMaterialDriver":
                if (F32(Field(d.Properties, "mValue")) is { } lit) bits.Add(Fmt(lit));
                break;
            default:
                if (ConditionOf(d, resolve) is { } cond && cond.Key != cond.Kind) bits.Add(cond.Label);
                break;
        }
        return bits.Count == 0 ? name : $"{name}({string.Join(", ", bits)})";
    }

    /// <summary>A bool driver named for a reason string - the condition's label, or its shape.</summary>
    private static string GateName(BinTreeStruct b, Func<uint, string?> resolve) =>
        ConditionOf(b, resolve) is { } c ? Short(ClassName(b, resolve)) + (c.Key == c.Kind ? "" : $"({c.Label})")
                                         : Describe(b, resolve);

    private static string ClassName(BinTreeStruct s, Func<uint, string?> resolve) => resolve(s.ClassHash) ?? $"0x{s.ClassHash:x8}";

    /// <summary>IsDeadDynamicMaterialBoolDriver -> IsDead, LerpMaterialDriver -> Lerp.</summary>
    internal static string Short(string cls)
    {
        string s = cls.Replace("DynamicMaterial", "", StringComparison.Ordinal).Replace("Material", "", StringComparison.Ordinal);
        if (s.EndsWith("Driver", StringComparison.Ordinal)) s = s[..^6];
        if (s.EndsWith("Bool", StringComparison.Ordinal)) s = s[..^4];
        else if (s.EndsWith("Float", StringComparison.Ordinal)) s = s[..^5];
        if (s.EndsWith("Logic", StringComparison.Ordinal)) s = s[..^5];
        return s.Length == 0 ? cls : s;
    }

    private static float? F32(BinTreeProperty? p) => p is BinTreeF32 f ? f.Value : null;
    private static Vector4 V(float x) => new(x, 0f, 0f, 0f);
    private static Vector4? V(float? f) => f is { } x ? new Vector4(x, 0f, 0f, 0f) : null;

    /// <summary>A written value of either shape - a scalar lerp branch or a colour one.</summary>
    private static Vector4? Value4(BinTreeProperty? p) => p switch
    {
        BinTreeF32 f => new Vector4(f.Value, 0f, 0f, 0f),
        _ => Colour(p),
    };

    private static Vector4? Colour(BinTreeProperty? p) => p switch
    {
        BinTreeColor c => c.Value,
        BinTreeVector4 v => v.Value,
        BinTreeVector3 v3 => new Vector4(v3.Value, 1f),
        BinTreeVector2 v2 => new Vector4(v2.Value.X, v2.Value.Y, 0f, 0f),
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
