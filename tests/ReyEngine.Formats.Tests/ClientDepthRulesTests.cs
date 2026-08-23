using ReyEngine.App.ViewModels;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M557: the editor was kinder than the game, so a decal read correctly here and went black there.
///
/// <para>M279 stopped transparents writing depth and pushed them into a tail after solid geometry,
/// because a decal that stamps depth at its own plane depth-rejects the ground it should composite over
/// and comes out BLACK. That fixed the editor; the client derives depth-write from the shader CLASS, so
/// it still does the old thing. The reporter isolated it exactly: at 6/7 the editor is clean and the game
/// is black.</para>
/// </summary>
public sealed class ClientDepthRulesTests
{
    [Fact]
    public void TheEmulationIsOffUntilAskedFor()
    {
        // A diagnostic, not an authoring mode: on, every transparent keeps the depth mask, which is wrong
        // for looking at a map and right for predicting what the client draws.
        Assert.False(new MainWindowViewModel().ClientDepthRules);
    }

    [Fact]
    public void TheToggleReachesTheSceneBuilder()
    {
        // The flag lives on the builder because that is where the per-material depth state is baked. A
        // toggle that only sets a view-model property would appear to work and change nothing.
        bool original = ReyEngine.App.Services.Dx11SceneBuilder.EmulateClientDepthRules;
        try
        {
            var vm = new MainWindowViewModel { ClientDepthRules = true };
            Assert.True(ReyEngine.App.Services.Dx11SceneBuilder.EmulateClientDepthRules);
            vm.ClientDepthRules = false;
            Assert.False(ReyEngine.App.Services.Dx11SceneBuilder.EmulateClientDepthRules);
        }
        finally { ReyEngine.App.Services.Dx11SceneBuilder.EmulateClientDepthRules = original; }
    }

    [Fact]
    public void TheAuthoredBlendSurvivesTheEmulation()
    {
        // The mode forces depthWrite true. UsesAuthoredColorBlend used to be keyed off !depthWrite, so
        // reading it after the override would silently drop the authored blend - and a decal that stops
        // compositing altogether hides the very thing the mode exists to show.
        string? file = RepoFile("src", "ReyEngine.App", "Services", "Dx11SceneBuilder.cs");
        if (file is null) return;
        string source = File.ReadAllText(file);

        Assert.Contains("mat.UsesAuthoredColorBlend = !s.Profile.DepthWrite && s.Profile.BlendEnabled;", source);
        Assert.DoesNotContain("mat.UsesAuthoredColorBlend = !depthWrite", source);
    }

    [Fact]
    public void TheOverrideIsAppliedBeforeTheDepthStateIsRead()
    {
        string? file = RepoFile("src", "ReyEngine.App", "Services", "Dx11SceneBuilder.cs");
        if (file is null) return;
        string source = File.ReadAllText(file);

        int applied = source.IndexOf("if (EmulateClientDepthRules) depthWrite = true;", StringComparison.Ordinal);
        int used = source.IndexOf("mat.WritesDepth = depthWrite;", StringComparison.Ordinal);
        Assert.True(applied > 0 && used > applied,
            "the override has to run before WritesDepth is read, or it does nothing");
    }

    [Fact]
    public void TheViewportToggleIsBoundToARealProperty()
    {
        // Avalonia resolves these at RUNTIME even with x:DataType - a binding to a name that does not
        // exist compiles cleanly and fails when the window is shown (proven in LegacyDecalQuadTests by
        // pointing one at a nonexistent property and watching the build succeed).
        //
        // Scoped to this toggle rather than sweeping the whole window: MainWindow.axaml nests templates
        // whose DataContext is a log entry, a tab, an outliner row - names like IsActive and Glyph live on
        // types outside ReyEngine.App.ViewModels, so a blanket sweep reports them and means nothing.
        string? file = RepoFile("src", "ReyEngine.App", "Views", "MainWindow.axaml");
        if (file is null) return;

        Assert.Contains("IsChecked=\"{Binding ClientDepthRules}\"", File.ReadAllText(file));
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("ClientDepthRules"));
    }

    private static string? RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? path : null;
    }
}
