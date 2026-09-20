using System.Text.RegularExpressions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M745: the material editor's item templates are compile-checked.
///
/// <para>The view declares <c>x:CompileBindings="True"</c>, but that only reaches a
/// <c>DataTemplate</c> that says what its rows hold. An untyped one falls back to reflection, and the
/// bindings inside it stop being checked by anything - including five buttons that reach the material's
/// view model through <c>$parent[ItemsControl].DataContext</c>, a path that resolves to nothing and says
/// nothing when the property behind it is renamed.</para>
///
/// <para><b>This changed no behaviour.</b> Measured before and after with a headless probe that opens the
/// real view over Map453's materials: the same 13 chip buttons render and all of them carry their command
/// either way. What it buys is the next rename - it becomes a build error instead of a dead button.</para>
/// </summary>
public sealed class MaterialEditorMarkupTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static string View() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "ReyEngine.App", "Views", "MaterialEditorView.axaml"));

    [Fact]
    public void EveryItemTemplateSaysWhatItsRowsHold()
    {
        string xaml = View();
        Assert.Contains("x:CompileBindings=\"True\"", xaml);

        int all = Regex.Matches(xaml, "<DataTemplate[ >]").Count;
        int typed = Regex.Matches(xaml, "<DataTemplate x:DataType=").Count;
        Assert.True(all > 0);
        Assert.Equal(all, typed);
    }

    [Fact]
    public void TheButtonsThatReachPastTheirRowNameTheViewModelTheyReachFor()
    {
        // Under compiled bindings the DataContext of an ancestor is typed only where it is cast, and
        // without the cast the command silently resolves to nothing.
        string xaml = View();
        foreach (string command in new[]
        {
            "AddShaderSamplerCommand", "AddShaderSwitchCommand", "AddMacroCommand",
            "AddShaderParameterCommand", "RemoveParameterCommand",
        })
            Assert.Contains($"$parent[ItemsControl].((vm:MaterialBindingViewModel)DataContext).{command}", xaml);

        Assert.DoesNotContain("$parent[ItemsControl].DataContext.", xaml);
    }
}
