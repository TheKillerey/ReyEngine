using System.Text.RegularExpressions;
using ReyEngine.Core.Settings;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M602: the registry and the controls that bind to it have to move together.
///
/// <para>The highlight system shipped in M593 and pointed at nothing for a whole release: the registry
/// was empty pending an editorial decision, and the only <c>NewFeature[...]</c> in the codebase was a
/// usage example inside a comment. It compiled, the tests passed, and the feature was invisible — which
/// is exactly what the author found when they went looking for the glow.</para>
///
/// <para>Both halves fail silently by design, which is why neither half can be checked alone:</para>
/// <list type="bullet">
///   <item>A control bound to an id the registry does not know goes quiet — <see cref="NewFeatures.IsNew"/>
///   returns false for an unknown id, deliberately, so a typo cannot glow forever with nothing able to
///   mark it seen.</item>
///   <item>A registry entry nothing binds to highlights nowhere, and still appears in a What's New list as
///   though it were being pointed out.</item>
/// </list>
///
/// <para>So this reads the ids straight out of the XAML and checks the two sets are equal.</para>
/// </summary>
/// <summary>Names the collection that serialises every test touching the static registry.</summary>
[CollectionDefinition(Name)]
public sealed class NewFeatureRegistryCollection
{
    public const string Name = "NewFeatureRegistry";
}

[Collection(NewFeatureRegistryCollection.Name)]
public sealed class NewFeatureRegistryWiringTests
{
    /// <summary>NewFeatures.SetRegistry mutates static state and other tests use it, so every test here
    /// pins the registry back to what this build ships before asserting.</summary>
    public NewFeatureRegistryWiringTests() => NewFeatures.SetRegistry(NewFeatures.Shipping);

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    /// <summary>Every id any .axaml binds, excluding the documentation example in the theme.</summary>
    private static HashSet<string> BoundIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (RepoRoot() is not { } root) return ids;
        string views = Path.Combine(root, "src", "ReyEngine.App");
        if (!Directory.Exists(views)) return ids;

        foreach (string file in Directory.GetFiles(views, "*.axaml", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"NewFeature\[\s*([A-Za-z0-9\-_]+)\s*\]"))
            {
                string id = m.Groups[1].Value;
                // ReyTheme.axaml carries "NewFeature[some-feature-id]" in a comment showing how to mark a
                // control. It is documentation, not a binding, and must not be mistaken for one.
                if (id.Equals("some-feature-id", StringComparison.OrdinalIgnoreCase)) continue;
                ids.Add(id);
            }
        return ids;
    }

    [Fact]
    public void TheRegistryIsNotEmpty()
    {
        // The M593 state: shipped, correct, and pointing at nothing.
        Assert.NotEmpty(NewFeatures.Shipping);
    }

    [Fact]
    public void EveryRegisteredFeatureIsBoundBySomeControl()
    {
        var bound = BoundIds();
        if (bound.Count == 0) return;   // sources not present (packaged test run)

        // M737: a NOTE is exempt by definition - it belongs to a release that added no entry point, so
        // there is nothing for it to glow on. Every entry that DOES carry an id still has to be bound.
        var unbound = NewFeatures.Shipping.Where(f => f.HasControl).Select(f => f.Id)
            .Where(id => !bound.Contains(id)).ToList();
        Assert.True(unbound.Count == 0,
            "registered but nothing binds them, so they highlight nowhere: " + string.Join(", ", unbound));
    }

    /// <summary>M737: a release whose work is performance and fixes still reaches What's New. v0.4.8
    /// shipped with no entry of any kind and the window skipped the release entirely.</summary>
    [Fact]
    public void AReleaseThatAddsNoEntryPointStillHasItsOwnLines()
    {
        var notes = NewFeatures.Shipping.Where(f => !f.HasControl).ToList();
        Assert.NotEmpty(notes);
        Assert.All(notes, n => Assert.False(string.IsNullOrWhiteSpace(n.Label)));
        // a note never glows - nothing is bound to it, and IsNew must not claim otherwise
        Assert.All(notes, n => Assert.False(NewFeatures.IsNew(n.Id, "0.3.1")));

        // ...but it IS listed, and marked NEW for someone who has not acknowledged that release
        var rows = ReyEngine.App.ViewModels.WhatsNewRows.Build(NewFeatures.Shipping, "0.4.7");
        foreach (var n in notes)
        {
            var row = rows.FirstOrDefault(r => !r.IsHeader && r.Label == n.Label);
            Assert.True(row is not null, $"the note '{n.Label}' is not in the What's New list");
            Assert.True(row!.IsNew, $"the note '{n.Label}' is not marked NEW for a user coming from 0.4.7");
        }
        // and the release this build ships is the newest one the list knows
        Assert.Equal(NewFeatures.CurrentVersion, rows.First(r => r.IsHeader).Version);
    }

    [Fact]
    public void EveryBoundIdIsInTheRegistry()
    {
        var bound = BoundIds();
        if (bound.Count == 0) return;

        var known = NewFeatures.Shipping.Where(f => f.HasControl).Select(f => f.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = bound.Where(id => !known.Contains(id)).ToList();
        Assert.True(unknown.Count == 0,
            "bound in XAML but unknown to the registry, so they silently never glow: "
            + string.Join(", ", unknown));
    }

    [Fact]
    public void EveryFeatureGlowsForSomeoneComingFromTheLastRelease()
    {
        // The whole point: a user updating from v0.3.1 sees all of them.
        Assert.All(NewFeatures.Shipping.Where(f => f.HasControl), f => Assert.True(NewFeatures.IsNew(f.Id, "0.3.1"),
            $"{f.Id} would not glow for a user coming from 0.3.1"));
    }

    [Fact]
    public void AFreshInstallWithNoRecordedVersionSeesThemToo()
    {
        // LastSeenFeatureVersion defaults to empty, and empty sorts lowest.
        Assert.All(NewFeatures.Shipping.Where(f => f.HasControl), f => Assert.True(NewFeatures.IsNew(f.Id, "")));
    }

    [Fact]
    public void NothingGlowsOnceThisReleaseIsAcknowledged()
    {
        Assert.All(NewFeatures.Shipping, f => Assert.False(NewFeatures.IsNew(f.Id, NewFeatures.CurrentVersion)));
        Assert.False(NewFeatures.AnyUnseen(NewFeatures.CurrentVersion));
    }

    [Fact]
    public void NoEntryIsFromTheFutureAndThisReleaseContributesOne()
    {
        // Until v0.4.1 this asserted every entry carried CurrentVersion, which only held while the
        // registry described exactly one release. The rest of the class is built for more than one - a
        // feature carries the version it arrived in, IsNew compares it against what the user has
        // acknowledged, and Unseen orders by version descending - so an entry from an earlier release is
        // correct and is what lets someone updating across two releases see both. What must not happen is
        // an entry from a release that has not shipped: it would still glow after this one is
        // acknowledged, with nothing able to clear it. And a release that highlights nothing is a mistake
        // in the other direction.
        Assert.All(NewFeatures.Shipping, f =>
            Assert.True(NewFeatures.Compare(f.Version, NewFeatures.CurrentVersion) <= 0,
                $"{f.Id} is marked {f.Version}, which is newer than the current release {NewFeatures.CurrentVersion}"));
        Assert.Contains(NewFeatures.Shipping, f => f.Version == NewFeatures.CurrentVersion);
    }

    [Fact]
    public void EveryEntryHasALabelThatReadsAsASentence()
    {
        Assert.All(NewFeatures.Shipping, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Label));
            // The label is what a What's New list shows, so it has to read as a sentence, not as an id.
            Assert.DoesNotContain('-', f.Id.Length > 0 ? f.Label.Split(' ')[0] : "x");
        });
    }

    [Fact]
    public void IdsAreUniqueAndLowercaseKebab()
    {
        // M737: a note carries no id - nothing binds it - so uniqueness and the kebab shape are about the
        // entries that DO name a control. Two notes sharing "" is not a collision.
        var named = NewFeatures.Shipping.Where(f => f.HasControl).ToList();
        Assert.Equal(named.Count, named.Select(f => f.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(named, f => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", f.Id));
    }

    /// <summary>Nothing confidential, internal or unfinished may appear — the registry is the thing that
    /// decides what gets named in the UI.</summary>
    [Fact]
    public void NoEntryNamesSomethingThatIsNotPublic()
    {
        string[] forbidden = { "mantis", "pbr", "ggx", "experimental", "debug", "internal", "wip", "todo" };
        foreach (var f in NewFeatures.Shipping)
            foreach (string word in forbidden)
            {
                Assert.DoesNotContain(word, f.Id, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(word, f.Label, StringComparison.OrdinalIgnoreCase);
            }
    }
}
