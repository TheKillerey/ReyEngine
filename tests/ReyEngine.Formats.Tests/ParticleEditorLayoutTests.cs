namespace ReyEngine.Formats.Tests;

/// <summary>
/// M716: the particle editor takes the shape a particle editor has.
///
/// <para>Five panes, the layout Unreal's Cascade established and every tool since has borrowed: the system
/// list down the left, the emitters across the middle, the details on the right, and the curve editor
/// beside the 3D view along the bottom. Before this the curve plot and the preview were both squeezed into
/// the centre column under the emitter cards, which left the plot about a third of the window wide and the
/// preview smaller than one emitter card - and the list and the details panes took the full height beside
/// them, so the window's two widest things were its two narrowest panes.</para>
///
/// <para>Layout only. Nothing about what the panes contain changed, which is why this is separable from
/// the module cards and the editable curve graph that should follow it.</para>
/// </summary>
public sealed class ParticleEditorLayoutTests
{
    private static string View()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine(dir!.FullName, "src", "ReyEngine.App", "Views", "ParticleEditorView.axaml");
        Assert.True(File.Exists(path), path);
        return File.ReadAllText(path);
    }

    [Fact]
    public void EveryPaneSaysWhatItIs()
    {
        string view = View();
        foreach (var pane in new[] { "SYSTEMS", "EMITTERS", "DETAILS", "3D VIEW" })
            Assert.Contains($"Classes=\"panelHeader\" DockPanel.Dock=\"Top\" Text=\"{pane}\"", view);
        Assert.Contains("Text=\"CURVE EDITOR (over particle lifetime)\"", view);
    }

    [Fact]
    public void TheBottomRowIsFullWidth()
    {
        string view = View();
        // two rows, and the bottom one is a child of the OUTER grid rather than of the centre column -
        // that is the whole of the change
        Assert.Contains("<Grid Grid.Row=\"2\" ColumnDefinitions=\"*,4,620\">", view);
        Assert.Contains("<GridSplitter Grid.Row=\"1\" Background=", view);
        Assert.DoesNotContain("<Grid Grid.Column=\"2\" Grid.Row=\"2\"", view);

        // and the list and the details no longer span the full height beside it
        Assert.DoesNotContain("Grid.RowSpan=\"3\"", view);
    }

    [Fact]
    public void NoSplitterCanCollapseAPaneIntoNothing()
    {
        string view = View();
        // MainWindow states the reason at its own splitters: without a minimum a drag collapses a pane
        // into a layout that cannot be dragged back.
        Assert.Contains("<ColumnDefinition Width=\"230\" MinWidth=\"150\" />", view);
        Assert.Contains("<ColumnDefinition Width=\"*\" MinWidth=\"260\" />", view);
        Assert.Contains("<ColumnDefinition Width=\"290\" MinWidth=\"200\" />", view);
        Assert.Contains("<RowDefinition Height=\"*\" MinHeight=\"200\" />", view);
        Assert.Contains("<RowDefinition Height=\"300\" MinHeight=\"140\" />", view);
    }

    [Fact]
    public void TheThingsThatMustSurviveALayoutChangeDid()
    {
        string view = View();
        // the camera input layer has to stay after the viewport in the same Grid, or orbiting dies silently
        int viewport = view.IndexOf("x:Name=\"PreviewViewport\"", StringComparison.Ordinal);
        int input = view.IndexOf("x:Name=\"PreviewInput\"", StringComparison.Ordinal);
        Assert.True(viewport > 0 && input > viewport, "PreviewInput must be layered after the viewport");

        // and the viewport still reads the rig, wherever its panel sits (M721: in DETAILS)
        Assert.Contains("ParticleRig=\"{Binding Rig}\"", view);
        Assert.Contains("FROM THE SPELL RECORD", view);
    }

    [Fact]
    public void TheRigSitsInTheDetailsColumnNotOverTheView()
    {
        // M721: laid over the 3D view it covered most of the bottom row's preview at any usable height, and
        // the camera could not be orbited underneath it. It is docked at the foot of DETAILS instead.
        string view = View();
        int details = view.IndexOf("Text=\"DETAILS\"", StringComparison.Ordinal);
        int rig = view.IndexOf("IsChecked=\"{Binding IsRigStill}\"", StringComparison.Ordinal);
        int curve = view.IndexOf("Text=\"CURVE EDITOR (over particle lifetime)\"", StringComparison.Ordinal);
        int viewport = view.IndexOf("x:Name=\"PreviewViewport\"", StringComparison.Ordinal);
        Assert.True(details > 0 && rig > details && rig < curve && rig < viewport,
            "the rig panel belongs in the details column, before the bottom row");
        Assert.Contains("<Border DockPanel.Dock=\"Bottom\" BorderBrush=\"{DynamicResource ReyBorderBrush}\"", view);
        Assert.DoesNotContain("HorizontalAlignment=\"Right\" VerticalAlignment=\"Top\"", view);
    }
}
