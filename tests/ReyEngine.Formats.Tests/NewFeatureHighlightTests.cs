using ReyEngine.Core.Settings;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M593: which features still glow, and when they stop.
///
/// <para>The system is version-aware rather than release-specific: a feature carries the release it
/// arrived in, the user carries the newest release they have acknowledged, and a feature glows while the
/// first is newer than the second. That is what lets v0.5.0 reuse it with no code change.</para>
///
/// <para>The case that decides the design is a user UPDATING into a release: their settings file predates
/// the field, so it reads as empty, and empty must sort below every real version or they would see
/// nothing.</para>
/// </summary>
public sealed class NewFeatureHighlightTests : IDisposable
{
    private readonly IReadOnlyList<NewFeature> _original = NewFeatures.All;

    public NewFeatureHighlightTests() => NewFeatures.SetRegistry(new[]
    {
        new NewFeature("blender-link", "0.4.0", "Blender Link"),
        new NewFeature("older-thing", "0.3.0", "Something Older"),
        new NewFeature("future-thing", "0.5.0", "Something Later"),
    });

    public void Dispose() => NewFeatures.SetRegistry(_original);

    [Fact]
    public void AFreshInstallSeesTheCurrentReleasesFeatures()
    {
        Assert.True(NewFeatures.IsNew("blender-link", ""));
        Assert.True(NewFeatures.AnyUnseen(""));
    }

    [Fact]
    public void ASettingsFileWrittenBeforeThisExistedAlsoSeesThem()
    {
        // The updating user: the field is absent so it deserialises as "". This is the case the whole
        // feature exists for, and an empty value sorting HIGH would silently show them nothing.
        Assert.True(NewFeatures.IsNew("blender-link", null));
    }

    [Fact]
    public void AcknowledgingTheReleaseStopsItsHighlights()
    {
        Assert.False(NewFeatures.IsNew("blender-link", "0.4.0"));
        Assert.False(NewFeatures.IsNew("older-thing", "0.4.0"));
    }

    [Fact]
    public void ALaterReleasesFeaturesStillGlowAfterAcknowledgingThisOne()
    {
        // The reusability requirement: 0.5.0 features must survive a 0.4.0 acknowledgement.
        Assert.True(NewFeatures.IsNew("future-thing", "0.4.0"));
        Assert.True(NewFeatures.AnyUnseen("0.4.0"));
        Assert.False(NewFeatures.AnyUnseen("0.5.0"));
    }

    [Fact]
    public void AnUnknownIdNeverGlows()
    {
        // A control bound to a typo'd or retired id must go quiet rather than glow forever - nothing would
        // ever mark it seen.
        Assert.False(NewFeatures.IsNew("no-such-feature", ""));
        Assert.False(NewFeatures.IsNew("", ""));
        Assert.False(NewFeatures.IsNew(null, ""));
    }

    [Theory]
    [InlineData("0.4.0", "0.3.1", 1)]
    [InlineData("0.3.1", "0.4.0", -1)]
    [InlineData("0.4.0", "0.4.0", 0)]
    [InlineData("0.10.0", "0.9.0", 1)]     // numeric, not lexical - "0.10" > "0.9"
    [InlineData("0.4", "0.4.0", 0)]        // missing components are zero
    [InlineData("v0.4.0", "0.4.0", 0)]     // a leading v is tolerated
    [InlineData("0.4.0-beta", "0.4.0", 0)] // a suffix does not make it newer
    [InlineData("0.4.0", "", 1)]
    public void VersionsCompareNumerically(string left, string right, int expected)
        => Assert.Equal(expected, Math.Sign(NewFeatures.Compare(left, right)));

    [Fact]
    public void UnseenListsNewestFirst()
    {
        var unseen = NewFeatures.Unseen("0.3.0");
        Assert.Equal(new[] { "future-thing", "blender-link" }, unseen.Select(f => f.Id));
    }

    [Fact]
    public void TheShippingRegistryNeverNamesAnythingConfidential()
    {
        // The registry decides what the UI exposes, so a confidential name must never reach it. Checked
        // against the REAL list, not the test one.
        NewFeatures.SetRegistry(_original);
        string[] forbidden = { "mantis", "pbr", "experimental", "debug", "internal", "prototype", "spike" };
        foreach (var feature in NewFeatures.All)
            foreach (string word in forbidden)
            {
                Assert.DoesNotContain(word, feature.Id, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(word, feature.Label, StringComparison.OrdinalIgnoreCase);
            }
    }
}

/// <summary>
/// M593: Preferences ▸ Save must not reset settings the dialog does not show.
///
/// <para>It did. <c>ToSettings()</c> built a brand-new <see cref="EditorSettings"/>, so
/// <c>FirstRunCompleted</c> was written back as false on every save and the first-run wizard reappeared on
/// the next launch. A "last seen features" field would have been wiped exactly the same way, which is why
/// the fix is structural — the round trip now starts from what was loaded.</para>
/// </summary>
public sealed class SettingsRoundTripTests
{
    [Fact]
    public void CloneCarriesTheFieldsTheDialogDoesNotEdit()
    {
        var saved = new EditorSettings
        {
            FirstRunCompleted = true,
            LastSeenFeatureVersion = "0.4.0",
            FlySpeed = 1234.5,
        };

        var clone = saved.Clone();

        Assert.True(clone.FirstRunCompleted);
        Assert.Equal("0.4.0", clone.LastSeenFeatureVersion);
        Assert.Equal(1234.5, clone.FlySpeed);
    }

    [Fact]
    public void CopyFromCarriesTheNewField()
    {
        var target = new EditorSettings();
        target.CopyFrom(new EditorSettings { LastSeenFeatureVersion = "0.4.0" });
        Assert.Equal("0.4.0", target.LastSeenFeatureVersion);
    }

    [Fact]
    public void ADefaultSettingsFileHasSeenNothing()
    {
        // Which is what makes an updating user see the current release's highlights.
        Assert.Equal("", new EditorSettings().LastSeenFeatureVersion);
        Assert.True(NewFeatures.Compare(NewFeatures.CurrentVersion, new EditorSettings().LastSeenFeatureVersion) > 0);
    }
}
