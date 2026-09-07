using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using System.Text.Json;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M652: the polarity of the emitter flags M193 called unreadable.
///
/// <para>M193 parked 43 unread emitter fields and recorded that several of the bools "ship ONLY true, so
/// their polarity is unreadable from the corpus at all". That was true when it was written. The
/// meta-class database arrived afterwards and DECLARES each field's default, and the corpus then reads
/// cleanly: every one of these is written only when it differs from its default, so the value is the
/// non-default one and the field's presence is the signal.</para>
///
/// <para>The trap this closes is <c>isLocalOrientation</c>. Its declared default is TRUE and it is written
/// FALSE in 162,164 of 162,164 occurrences, so a renderer reading <c>?? false</c> would be wrong for every
/// emitter that omits it - which is most of them. M193's own note on that field said "always true",
/// contradicting its class remarks, whose list of seven all-true bools never included it.</para>
///
/// <para><b>Polarity is not semantics.</b> These tests pin what a field READS, not what it should DO;
/// that is still unknown and the corpus cannot settle it - see the class remarks on VfxEmitterExtras.</para>
/// </summary>
public sealed class VfxFlagPolarityTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    /// <summary>The default VfxEmitterDefinitionData declares for a field, straight out of the schema
    /// database the editor ships - so this table cannot drift away from Riot's own.</summary>
    private static JsonElement? DeclaredDefault(string field)
    {
        string path = Path.Combine(RepoRoot(), "data", "meta", "meta.db.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var classes = doc.RootElement.GetProperty("classes");
        if (!classes.TryGetProperty("0x9cde442", out var emitter)) return null;   // VfxEmitterDefinitionData
        foreach (var p in emitter.GetProperty("properties").EnumerateObject())
            if (p.Value.TryGetProperty("name", out var n) && n.GetString() == field)
            {
                var revs = p.Value.GetProperty("revisions");
                var latest = revs[revs.GetArrayLength() - 1];
                return latest.TryGetProperty("default", out var d) ? d.Clone() : null;
            }
        return null;
    }

    [Fact]
    public void TheFlagWhoseDefaultIsTrueIsTheOneThatCanBeReadBackwards()
    {
        var declared = DeclaredDefault("isLocalOrientation");
        if (declared is null) return;
        Assert.Equal(JsonValueKind.True, declared.Value.ValueKind);

        // and the accessor honours it, which is the whole point of adding one
        Assert.True(new VfxEmitterExtras().IsLocalOrientationOrDefault);
        Assert.True(new VfxEmitterExtras { IsLocalOrientation = null }.IsLocalOrientationOrDefault);
        Assert.False(new VfxEmitterExtras { IsLocalOrientation = false }.IsLocalOrientationOrDefault);
    }

    [Fact]
    public void EveryFlagInTheTableDefaultsToWhatTheClassRemarksSay()
    {
        // The remarks on VfxEmitterExtras list a declared default per field. Checked against the schema
        // rather than kept by memory - a schema change should break a test, not a preview.
        var expected = new (string Field, JsonValueKind Kind, double? Number)[]
        {
            ("isUniformScale", JsonValueKind.False, null),
            ("isGroundLayer", JsonValueKind.False, null),
            ("isLocalOrientation", JsonValueKind.True, null),
            ("particleIsLocalOrientation", JsonValueKind.False, null),
            ("useNavmeshMask", JsonValueKind.False, null),
            ("isRotationEnabled", JsonValueKind.False, null),
            ("meshRenderFlags", JsonValueKind.Number, 1),
            ("miscRenderFlags", JsonValueKind.Number, 0),
        };
        foreach (var (field, kind, number) in expected)
        {
            var declared = DeclaredDefault(field);
            if (declared is null) return;   // no schema database on this machine
            Assert.Equal(kind, declared.Value.ValueKind);
            if (number is { } n) Assert.Equal(n, declared.Value.GetDouble());
        }
    }

    [Fact]
    public void TheCorpusOnlyEverWritesTheNonDefaultValue()
    {
        // The claim the class remarks rest on, checked on a real champion: a flag that appears at all
        // appears with the value that is NOT its declared default.
        string wad = @"C:\Riot Games\League of Legends\Game\DATA\FINAL\Champions\Aatrox.wad.client";
        if (!File.Exists(wad)) return;
        HashDatabase db;
        try { db = new HashSyncService().LoadLocal(_ => { }); }
        catch { return; }

        uint emitterClass = HashAlgorithms.Fnv1a("VfxEmitterDefinitionData");
        var flags = new Dictionary<uint, (string Name, bool NonDefault)>
        {
            [HashAlgorithms.Fnv1a("isUniformScale")] = ("isUniformScale", true),
            [HashAlgorithms.Fnv1a("isGroundLayer")] = ("isGroundLayer", true),
            [HashAlgorithms.Fnv1a("isLocalOrientation")] = ("isLocalOrientation", false),
            [HashAlgorithms.Fnv1a("particleIsLocalOrientation")] = ("particleIsLocalOrientation", true),
            [HashAlgorithms.Fnv1a("useNavmeshMask")] = ("useNavmeshMask", true),
            [HashAlgorithms.Fnv1a("isRotationEnabled")] = ("isRotationEnabled", true),
        };

        using var archive = WadArchive.Open(wad, new WadPathResolver(db));
        int checkedFlags = 0;
        foreach (var entry in archive.Entries.Where(e => e.IsResolved && e.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
        {
            BinTree tree;
            try { tree = new BinTree(new MemoryStream(archive.Extract(entry.PathHash))); }
            catch { continue; }
            foreach (var obj in tree.Objects.Values) Walk(obj.ClassHash, obj.Properties);

            void Walk(uint cls, IReadOnlyDictionary<uint, BinTreeProperty> props)
            {
                if (cls == emitterClass)
                    foreach (var (h, p) in props)
                        if (flags.TryGetValue(h, out var f))
                        {
                            bool? value = p switch
                            {
                                BinTreeBool b => b.Value,
                                BinTreeBitBool b => b.Value,
                                _ => null,
                            };
                            if (value is null) continue;
                            checkedFlags++;
                            Assert.True(value == f.NonDefault,
                                $"{f.Name} was written {value} - the corpus is supposed to only ever write {f.NonDefault}");
                        }
                foreach (var p in props.Values)
                    foreach (var s in Nested(p)) Walk(s.ClassHash, s.Properties);
            }
        }
        Assert.True(checkedFlags > 0, "no emitter flag was found to check");
    }

    private static IEnumerable<BinTreeStruct> Nested(
        BinTreeProperty p)
    {
        switch (p)
        {
            case BinTreeStruct s: yield return s; break;
            case BinTreeOptional o
                when o.Value is BinTreeStruct os: yield return os; break;
            case BinTreeContainer c:
                foreach (var e in c.Elements)
                    if (e is BinTreeStruct es) yield return es;
                break;
        }
    }
}
