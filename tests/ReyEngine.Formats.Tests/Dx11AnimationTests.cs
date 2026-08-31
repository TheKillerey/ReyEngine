using System.Reflection;
using System.Text.RegularExpressions;
using ReyEngine.App.ViewModels;
using ReyEngine.Rendering.D3D11;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M615: the D3D11 renderer can pose a skeleton, and something actually drives it.
///
/// <para>The second half matters as much as the first. A palette the renderer accepts but nothing ever
/// fills is a capability that looks shipped and does nothing — the exact shape of the dead-overload trap
/// that hid a lighting gate here for four milestones — so these assert that the driver exists, is
/// reachable from the window, and defaults to the behaviour that was already correct.</para>
/// </summary>
public sealed class Dx11AnimationTests
{
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName;
    }

    [Fact]
    public void TheRendererTakesABonePalette()
    {
        // The field the whole milestone hangs off. Named and typed so a rename cannot silently orphan
        // the driver below.
        var field = typeof(PreviewSettings).GetField("BonePalette");
        Assert.NotNull(field);
        Assert.Equal(typeof(System.Numerics.Matrix4x4[]), field!.FieldType);
    }

    [Fact]
    public void ThePaletteDefaultsToNullSoNothingElseChangesBehaviour()
    {
        // Null means "keep the M216 constant", which is the bind pose. Every map and every non-skinned
        // draw in the app goes through this path and must be untouched by M615.
        Assert.Null(new PreviewSettings().BonePalette);
    }

    [Fact]
    public void TheShaderPreviewOwnsTheDriver()
    {
        var type = typeof(ShaderPreviewViewModel);
        foreach (string name in new[] { "Animations", "SelectedAnimation", "AnimationPlaying",
                                        "AnimationSpeed", "AnimationStatus", "HasSkeleton", "HasAnimations" })
            Assert.NotNull(type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void TheDriverIsReachableFromTheWindow()
    {
        // A view model that computes a palette nobody can select a clip for is still a dead path.
        if (RepoRoot() is not { } root) return;
        string xaml = Path.Combine(root, "src", "ReyEngine.App", "Views", "ShaderPreviewWindow.axaml");
        if (!File.Exists(xaml)) return;

        string text = File.ReadAllText(xaml);
        var card = Regex.Match(text, @"Text=""ANIMATION""(.|\n)*?</StackPanel>");
        Assert.True(card.Success, "the ANIMATION block is missing from ShaderPreviewWindow.axaml");

        Type[] contexts = { typeof(ShaderPreviewViewModel), typeof(PreviewAnimationRow) };
        var missing = new List<string>();
        foreach (Match m in Regex.Matches(card.Value, @"\{Binding\s+!?([A-Za-z_][A-Za-z0-9_]*)"))
        {
            string path = m.Groups[1].Value;
            if (path is "Binding") continue;
            if (!contexts.Any(t => t.GetProperty(path, BindingFlags.Public | BindingFlags.Instance) is not null))
                missing.Add(path);
        }
        Assert.True(missing.Count == 0,
            "bound in the ANIMATION block but on neither context: " + string.Join(", ", missing.Distinct()));
    }

    [Fact]
    public void TheHostHandsOverTheClips()
    {
        // The window can only offer clips it was given. This is the one link in the chain that lives in
        // the main window and is invisible to every other test here.
        if (RepoRoot() is not { } root) return;
        string source = Path.Combine(root, "src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs");
        if (!File.Exists(source)) return;

        string text = File.ReadAllText(source);
        Assert.Contains("animationAssets:", text);
        Assert.Contains(".anm\"", text);
    }

    [Fact]
    public void TheConstructorAcceptsAnimationAssets()
    {
        var ctor = typeof(ShaderPreviewViewModel).GetConstructors().Single();
        Assert.Contains(ctor.GetParameters(), p => p.Name == "animationAssets");
        // Optional, so every existing caller keeps working with no clips and a bind pose.
        Assert.True(ctor.GetParameters().Single(p => p.Name == "animationAssets").IsOptional);
    }
}
