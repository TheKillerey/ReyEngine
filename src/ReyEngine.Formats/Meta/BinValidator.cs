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

    public static BinValidationReport Validate(
        string binName, byte[] binBytes,
        IReadOnlyList<byte[]> dependencyBins,
        Func<string, bool> assetExists,
        Func<uint, string?>? resolve = null,
        Func<uint, bool>? linkExempt = null)
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

        foreach (var (hash, obj) in tree.Objects)
        {
            string owner = R(hash);
            foreach (var v in obj.Properties.Values) Walk(v, owner, hash, obj.ClassHash);
        }

        return new BinValidationReport(binName, tree.Objects.Count, links, assets, issues);
    }

    /// <summary>Strings that reference files the game will try to load. Requires a path separator so
    /// bare names (submesh lists, event names) never false-positive.</summary>
    private static bool LooksLikeAssetPath(string s) =>
        s.Length > 5 && (s.Contains('/') || s.Contains('\\'))
        && AssetExts.Any(e => s.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
