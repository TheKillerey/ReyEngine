using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Meta;

namespace ReyEngine.Formats.Skeletons;

/// <summary>What a non-atomic entry of <c>animationGraphData.mClipDataMap</c> is. Atomic clips are <see cref="AnimClipInfo"/>.</summary>
public enum AnimClipNodeKind { Sequencer, Selector, Parametric, ConditionFloat, ConditionBool, Parallel, Other }

/// <summary>One child reference of a composite node: the child's clip-name hash and the value the node pairs with it
/// (a selector's probability, a parametric's parameter value, a float condition's threshold), or null when it authors none.</summary>
public sealed record AnimClipChild(uint NameHash, float? Value);

/// <summary>
/// M873: a non-atomic entry of an animation graph's clip map. Measured over the 173 champion base graphs (16.22): the clip map holds
/// <c>AtomicClipData</c> (one .anm), <c>SequencerClipData</c> (<c>mClipNameList</c>: clips in order), <c>SelectorClipData</c>
/// (<c>mSelectorPairDataList</c>: clip + probability), <c>ParametricClipData</c> (<c>mParametricPairDataList</c>: clip + parameter
/// value, driven by an Updater) and <c>ConditionFloatClipData</c> (<c>mConditionFloatPairDataList</c>: clip + optional threshold).
/// </summary>
public sealed record AnimClipNode(string Name, uint NameHash, AnimClipNodeKind Kind, string ClassName, IReadOnlyList<AnimClipChild> Children);

/// <summary>The atomic clips an authored name plays, in play order.</summary>
/// <param name="Kind">"atomic" or the composite kind that was walked ("sequencer", "selector", "parametric", "conditionFloat").</param>
/// <param name="Via">The composite names walked from the authored name down to the first clip (empty for an atomic clip).</param>
public sealed record AnimClipResolution(IReadOnlyList<AnimClipInfo> Chain, string Kind, IReadOnlyList<string> Via)
{
    public AnimClipInfo First => Chain[0];
    public bool IsComposite => Kind != "atomic";
}

/// <summary>
/// M873: the composite clips of an animation graph and how an authored clip name resolves through them.
///
/// <para>Riot's <c>mAnimationName</c> is a key of the clip map, and the key is not always an .anm: AurelionSol's Q authors
/// <c>Spell1</c>, which is a selector/sequencer over several files. The game plays the first atomic clip of the chain.
/// The walk is: atomic -> itself; sequencer -> its children in order (each expanded); selector -> its FIRST pair (the game
/// picks by probability, a preview needs one, and the first is the authored default); parametric -> the pair whose value is nearest
/// zero (the neutral parameter: no turn, no speed), ties first; float condition -> the pair that authors no threshold (the
/// "otherwise" branch), else the first; bool condition -> the FALSE branch (the condition not triggered), else the true one; parallel
/// -> its first (primary) layer. Depth and cycles are bounded.</para>
/// </summary>
public static class AnimationClipGraph
{
    private static readonly uint ClassSequencer = HashAlgorithms.Fnv1a("SequencerClipData");
    private static readonly uint ClassSelector = HashAlgorithms.Fnv1a("SelectorClipData");
    private static readonly uint ClassParametric = HashAlgorithms.Fnv1a("ParametricClipData");
    private static readonly uint ClassConditionFloat = HashAlgorithms.Fnv1a("ConditionFloatClipData");
    private static readonly uint ClassConditionBool = HashAlgorithms.Fnv1a("ConditionBoolClipData");
    private static readonly uint ClassParallel = HashAlgorithms.Fnv1a("ParallelClipData");
    private static readonly uint FTrueClip = HashAlgorithms.Fnv1a("mTrueConditionClipName");
    private static readonly uint FFalseClip = HashAlgorithms.Fnv1a("mFalseConditionClipName");
    private static readonly uint ClassAtomic = HashAlgorithms.Fnv1a("AtomicClipData");

    private static readonly uint FClipDataMap = HashAlgorithms.Fnv1a("mClipDataMap");
    private static readonly uint FClipNameList = HashAlgorithms.Fnv1a("mClipNameList");
    private static readonly uint FSelectorPairs = HashAlgorithms.Fnv1a("mSelectorPairDataList");
    private static readonly uint FParametricPairs = HashAlgorithms.Fnv1a("mParametricPairDataList");
    private static readonly uint FConditionFloatPairs = HashAlgorithms.Fnv1a("mConditionFloatPairDataList");
    private static readonly uint FClipName = HashAlgorithms.Fnv1a("mClipName");
    private static readonly uint FProbability = HashAlgorithms.Fnv1a("mProbability");
    private static readonly uint FValue = HashAlgorithms.Fnv1a("mValue");

    private const int MaxDepth = 12;

    /// <summary>The hash a clip is keyed by in the clip map.</summary>
    public static uint HashOf(string clipName)
    {
        if (clipName.Length == 10 && clipName.StartsWith("0x", StringComparison.Ordinal)
            && uint.TryParse(clipName.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint raw))
            return raw;
        return HashAlgorithms.Fnv1a(clipName);
    }

    /// <summary>Every non-atomic entry of the graph's clip map, in bin order. Never throws; empty on a malformed bin.</summary>
    public static IReadOnlyList<AnimClipNode> ParseNodes(byte[] animationBin, Func<uint, string?> resolve)
    {
        var nodes = new List<AnimClipNode>();
        try
        {
            var bin = SafeBinTree.Parse(animationBin);
            foreach (var o in bin.Objects.Values)
            {
                if (!o.Properties.TryGetValue(FClipDataMap, out var mapProp) || mapProp is not System.Collections.IEnumerable entries) continue;
                foreach (var kv in entries)
                {
                    var kvType = kv.GetType();
                    if (kvType.GetProperty("Value")?.GetValue(kv) is not BinTreeStruct s || s.ClassHash == ClassAtomic) continue;
                    uint nameHash = kvType.GetProperty("Key")?.GetValue(kv) switch { BinTreeHash h => h.Value, BinTreeU32 u => u.Value, _ => 0u };
                    string name = resolve(nameHash) ?? $"0x{nameHash:x8}";

                    var kind = AnimClipNodeKind.Other;
                    var children = new List<AnimClipChild>();
                    if (s.ClassHash == ClassSequencer)
                    {
                        kind = AnimClipNodeKind.Sequencer;
                        if (s.Properties.TryGetValue(FClipNameList, out var list) && list is BinTreeContainer c)
                            foreach (var el in c.Elements)
                                if (HashValue(el) is { } h) children.Add(new AnimClipChild(h, null));
                    }
                    else if (s.ClassHash == ClassParallel)
                    {
                        // layers played together; mClipNameList like a sequencer's, the FIRST is the primary layer
                        kind = AnimClipNodeKind.Parallel;
                        if (s.Properties.TryGetValue(FClipNameList, out var plist) && plist is BinTreeContainer pc)
                            foreach (var el in pc.Elements)
                                if (HashValue(el) is { } h) children.Add(new AnimClipChild(h, null));
                    }
                    else if (s.ClassHash == ClassConditionBool)
                    {
                        // Value 0 = the false branch, 1 = the true branch
                        kind = AnimClipNodeKind.ConditionBool;
                        if (s.Properties.TryGetValue(FFalseClip, out var fp) && HashValue(fp) is { } fh) children.Add(new AnimClipChild(fh, 0f));
                        if (s.Properties.TryGetValue(FTrueClip, out var tp) && HashValue(tp) is { } th) children.Add(new AnimClipChild(th, 1f));
                    }
                    else if (s.ClassHash == ClassSelector) { kind = AnimClipNodeKind.Selector; ReadPairs(s, FSelectorPairs, FProbability, children); }
                    else if (s.ClassHash == ClassParametric) { kind = AnimClipNodeKind.Parametric; ReadPairs(s, FParametricPairs, FValue, children); }
                    else if (s.ClassHash == ClassConditionFloat) { kind = AnimClipNodeKind.ConditionFloat; ReadPairs(s, FConditionFloatPairs, FValue, children); }
                    nodes.Add(new AnimClipNode(name, nameHash, kind, ClassName(s.ClassHash), children));
                }
            }
        }
        catch { /* malformed bin: return what was read */ }
        return nodes;
    }

    private static string ClassName(uint hash) =>
        hash == ClassSequencer ? "SequencerClipData"
        : hash == ClassSelector ? "SelectorClipData"
        : hash == ClassParametric ? "ParametricClipData"
        : hash == ClassConditionFloat ? "ConditionFloatClipData"
        : hash == ClassConditionBool ? "ConditionBoolClipData"
        : hash == ClassParallel ? "ParallelClipData"
        : $"0x{hash:x8}";

    private static uint? HashValue(BinTreeProperty p) => p switch { BinTreeHash h => h.Value, BinTreeU32 u => u.Value, _ => null };

    private static void ReadPairs(BinTreeStruct s, uint listField, uint valueField, List<AnimClipChild> into)
    {
        if (!s.Properties.TryGetValue(listField, out var list) || list is not BinTreeContainer c) return;
        foreach (var el in c.Elements)
        {
            if (el is not BinTreeStruct pair || !pair.Properties.TryGetValue(FClipName, out var n) || HashValue(n) is not { } h) continue;
            float? value = pair.Properties.TryGetValue(valueField, out var v)
                ? v switch { BinTreeF32 f => f.Value, BinTreeI32 i => i.Value, BinTreeU32 u => u.Value, _ => null }
                : null;
            into.Add(new AnimClipChild(h, value));
        }
    }

    /// <summary>
    /// The atomic clips the authored <paramref name="name"/> plays, or null when neither an atomic clip nor a composite node answers to it.
    /// <paramref name="atomics"/> are the clips of ONE graph (the caller decides whose); an atomic clip wins over a node of the same key.
    /// </summary>
    public static AnimClipResolution? Resolve(string name, IEnumerable<AnimClipInfo> atomics, IEnumerable<AnimClipNode> nodes)
    {
        var byHash = new Dictionary<uint, AnimClipInfo>();
        foreach (var c in atomics) byHash.TryAdd(HashOf(c.Name), c);
        var nodeByHash = new Dictionary<uint, AnimClipNode>();
        foreach (var n in nodes) nodeByHash.TryAdd(n.NameHash, n);

        uint root = HashOf(name);
        if (byHash.TryGetValue(root, out var exact)) return new AnimClipResolution(new[] { exact }, "atomic", Array.Empty<string>());
        if (!nodeByHash.TryGetValue(root, out var node)) return null;

        var chain = new List<AnimClipInfo>();
        var via = new List<string>();
        var visiting = new HashSet<uint>();
        Expand(root, 0, byHash, nodeByHash, visiting, chain, via, firstOnly: false);
        return chain.Count == 0 ? null : new AnimClipResolution(chain, KindText(node.Kind), via);
    }

    private static string KindText(AnimClipNodeKind k) => k switch
    {
        AnimClipNodeKind.Sequencer => "sequencer",
        AnimClipNodeKind.Selector => "selector",
        AnimClipNodeKind.Parametric => "parametric",
        AnimClipNodeKind.ConditionFloat => "conditionFloat",
        AnimClipNodeKind.ConditionBool => "conditionBool",
        AnimClipNodeKind.Parallel => "parallel",
        _ => "other",
    };

    private static void Expand(uint hash, int depth, Dictionary<uint, AnimClipInfo> atoms, Dictionary<uint, AnimClipNode> nodes,
        HashSet<uint> visiting, List<AnimClipInfo> chain, List<string> via, bool firstOnly)
    {
        if (depth > MaxDepth) return;
        if (atoms.TryGetValue(hash, out var atom)) { chain.Add(atom); return; }
        if (!nodes.TryGetValue(hash, out var node) || !visiting.Add(hash)) return;
        via.Add(node.Name);
        switch (node.Kind)
        {
            case AnimClipNodeKind.Sequencer:
                foreach (var ch in node.Children)
                {
                    int before = chain.Count;
                    Expand(ch.NameHash, depth + 1, atoms, nodes, visiting, chain, via, firstOnly: false);
                    if (firstOnly && chain.Count > before) break;
                }
                break;
            case AnimClipNodeKind.Parallel:
            case AnimClipNodeKind.ConditionBool:   // children are ordered false, true: the un-triggered branch first
            case AnimClipNodeKind.Selector:
                // the first pair that resolves
                foreach (var ch in node.Children)
                {
                    int before = chain.Count;
                    Expand(ch.NameHash, depth + 1, atoms, nodes, visiting, chain, via, firstOnly: true);
                    if (chain.Count > before) break;
                }
                break;
            case AnimClipNodeKind.Parametric:
                foreach (var ch in node.Children.Select((c, i) => (c, i))
                             .OrderBy(x => Math.Abs(x.c.Value ?? float.MaxValue)).ThenBy(x => x.i).Select(x => x.c))
                {
                    int before = chain.Count;
                    Expand(ch.NameHash, depth + 1, atoms, nodes, visiting, chain, via, firstOnly: true);
                    if (chain.Count > before) break;
                }
                break;
            case AnimClipNodeKind.ConditionFloat:
                foreach (var ch in node.Children.Select((c, i) => (c, i)).OrderBy(x => x.c.Value is null ? 0 : 1).ThenBy(x => x.i).Select(x => x.c))
                {
                    int before = chain.Count;
                    Expand(ch.NameHash, depth + 1, atoms, nodes, visiting, chain, via, firstOnly: true);
                    if (chain.Count > before) break;
                }
                break;
        }
        visiting.Remove(hash);
    }
}
