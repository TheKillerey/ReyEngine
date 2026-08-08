using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LtProp = LeagueToolkit.Core.Meta.BinTreeProperty;

namespace ReyEngine.Formats.Meta;

/// <summary>M127: <paramref name="ObjectPathHash"/>/<paramref name="ObjectClassHash"/> identify the
/// object the issue lives in (0 for file-level issues) so the UI can navigate to it and fix it.</summary>
public sealed record BinIssue(string Category, string ObjectName, string Detail,
    uint ObjectPathHash = 0, uint ObjectClassHash = 0);

public sealed record BinValidationReport(
    string BinName, int ObjectCount, int LinksChecked, int AssetRefsChecked,
    IReadOnlyList<BinIssue> Issues)
{
    public bool IsClean => Issues.Count == 0;

    /// <summary>M129: every file path this bin references (asset strings + dependency list) — feeds
    /// the is-anything-still-using-this-bin analysis.</summary>
    public IReadOnlyCollection<string> ReferencedPaths { get; init; } = Array.Empty<string>();
}

/// <summary>
/// M97: emulated-injection integrity check for a mod .bin. The caller supplies the merged view the game
/// would see (project overrides over Riot originals) via <paramref name="assetExists"/> and the bin's
/// resolvable dependency bins; the validator walks every property and reports what would break in-game:
/// object links pointing at nothing (the classic map11.bin crash) and referenced assets that don't exist
/// in the merged view. Read-only; never throws on malformed input (reported as an issue instead).
/// </summary>
public static class BinValidator
{
    private static readonly string[] AssetExts =
    {
        ".dds", ".tex", ".skn", ".skl", ".anm", ".bnk", ".wpk", ".scb", ".sco", ".mapgeo", ".bin",
    };

    /// <summary>M372: what the meta-class schema says about one property of one class, at the build the
    /// caller resolved for. All three delegates are optional - with no schema the validator behaves exactly
    /// as it did before, which matters because the schema is a separate opt-in download.</summary>
    /// <param name="classKnown">Is this class in the schema at all? Everything below is skipped when not:
    /// an unknown class means no expectations, not a bin full of problems.</param>
    /// <param name="declaredType">The field type declared for (class, property) at the target build, or
    /// null when the property is not declared there.</param>
    /// <param name="declaredEver">Was it declared at ANY build? Separates "removed in your patch" from
    /// "never existed", which need opposite advice.</param>
    public static BinValidationReport Validate(
        string binName, byte[] binBytes,
        IReadOnlyList<byte[]> dependencyBins,
        Func<string, bool> assetExists,
        Func<uint, string?>? resolve = null,
        Func<uint, bool>? linkExempt = null,
        Func<uint, bool>? classKnown = null,
        Func<uint, uint, string?>? declaredType = null,
        Func<uint, uint, bool>? declaredEver = null)
    {
        string R(uint h) => resolve?.Invoke(h) ?? $"0x{h:x8}";
        var issues = new List<BinIssue>();

        BinTree tree;
        try { tree = SafeBinTree.Parse(binBytes); }
        catch (Exception ex)
        {
            return new BinValidationReport(binName, 0, 0, 0,
                new[] { new BinIssue("parse-error", binName, ex.Message) });
        }

        // objects reachable by links: this bin + every dependency bin the caller could resolve
        var known = new HashSet<uint>(tree.Objects.Keys);
        foreach (var depBytes in dependencyBins)
        {
            try { foreach (var k in SafeBinTree.Parse(depBytes).Objects.Keys) known.Add(k); }
            catch { /* a broken dependency shows up via its own validation run */ }
        }

        // dependencies must exist in the merged view — the game hard-requires them
        foreach (var dep in tree.Dependencies)
            if (!assetExists(dep))
                issues.Add(new BinIssue("missing-dependency", binName, dep));

        int links = 0, assets = 0;
        var checkedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(LtProp p, string owner, uint ownerHash, uint ownerClass)
        {
            switch (p)
            {
                case BinTreeObjectLink link when link.Value != 0:
                    links++;
                    // linkExempt: links the game resolves globally (shader objects etc.), not via deps
                    if (!known.Contains(link.Value) && linkExempt?.Invoke(link.Value) != true)
                        issues.Add(new BinIssue("missing-link", owner,
                            $"link → {R(link.Value)} not found in this bin or its {tree.Dependencies.Count} dependencies",
                            ownerHash, ownerClass));
                    break;
                case BinTreeString s when LooksLikeAssetPath(s.Value):
                    if (checkedAssets.Add(s.Value))
                    {
                        assets++;
                        if (!assetExists(s.Value))
                            issues.Add(new BinIssue("missing-asset", owner, s.Value, ownerHash, ownerClass));
                    }
                    break;
                case BinTreeStruct st:
                    foreach (var v in st.Properties.Values) Walk(v, owner, ownerHash, ownerClass);
                    break;
                case BinTreeContainer c:
                    foreach (var el in c.Elements) Walk(el, owner, ownerHash, ownerClass);
                    break;
                default:
                    if (p is not BinTreeString && p is System.Collections.IEnumerable en)
                        foreach (var kv in en)
                        {
                            var t = kv?.GetType();
                            if (t?.GetProperty("Key")?.GetValue(kv) is LtProp kp) Walk(kp, owner, ownerHash, ownerClass);
                            if (t?.GetProperty("Value")?.GetValue(kv) is LtProp vp) Walk(vp, owner, ownerHash, ownerClass);
                        }
                    break;
            }
        }

        // M372: schema checks, on the object's OWN properties only.
        //
        // Top level only, on purpose. Nested structs each have their own class and would need the same
        // lookup per level; doing that without also handling embedded/optional/container element classes
        // would produce confident nonsense on the nested cases. Top-level properties are where a stale mod
        // actually breaks, and they are checkable with no guessing.
        void CheckSchema(uint objHash, BinTreeObject obj)
        {
            if (declaredType is null || classKnown?.Invoke(obj.ClassHash) != true) return;
            string owner = R(objHash);

            foreach (var (propHash, prop) in obj.Properties)
            {
                string? declared = declaredType(obj.ClassHash, propHash);
                if (declared is null)
                {
                    // The game reads a bin against the class it knows, so a field the class does not
                    // declare is simply ignored - it is not a crash. Reported because it is almost always
                    // either a stale field from an older patch or a hash that belongs to another class.
                    bool everExisted = declaredEver?.Invoke(obj.ClassHash, propHash) == true;
                    issues.Add(new BinIssue(
                        everExisted ? "field-removed-in-patch" : "field-not-in-class",
                        owner,
                        everExisted
                            ? $"{R(propHash)} is not part of {R(obj.ClassHash)} in this patch (it existed in "
                              + "an earlier build) — the game will ignore it"
                            : $"{R(propHash)} is not declared by {R(obj.ClassHash)} — the game will ignore it",
                        objHash, obj.ClassHash));
                    continue;
                }

                // Only where the mapping is unambiguous; see MetaDefaultProperty.ExpectedWireType for why
                // the container/struct families are deliberately not checked.
                string? expected = MetaDefaultProperty.ExpectedWireType(declared);
                if (expected is null) continue;
                string actual = prop.GetType().Name;
                if (!string.Equals(expected, actual, StringComparison.Ordinal))
                    issues.Add(new BinIssue("field-type-mismatch", owner,
                        $"{R(propHash)} is stored as {actual} but {R(obj.ClassHash)} declares {declared} "
                        + $"({expected}) — the game will read it at the wrong width",
                        objHash, obj.ClassHash));
            }
        }

        foreach (var (hash, obj) in tree.Objects)
        {
            string owner = R(hash);
            foreach (var v in obj.Properties.Values) Walk(v, owner, hash, obj.ClassHash);
            CheckSchema(hash, obj);
        }

        var refPaths = new HashSet<string>(checkedAssets, StringComparer.OrdinalIgnoreCase);
        foreach (var dep in tree.Dependencies) refPaths.Add(dep);
        return new BinValidationReport(binName, tree.Objects.Count, links, assets, issues)
        { ReferencedPaths = refPaths };
    }

    /// <summary>Strings that reference files the game will try to load. Requires a path separator so
    /// bare names (submesh lists, event names) never false-positive.</summary>
    private static bool LooksLikeAssetPath(string s) =>
        s.Length > 5 && (s.Contains('/') || s.Contains('\\'))
        && AssetExts.Any(e => s.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
