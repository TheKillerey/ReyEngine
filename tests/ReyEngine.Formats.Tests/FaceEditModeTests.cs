using ReyEngine.App.ViewModels;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M566: the face-edit mode's wiring.
///
/// <para>Face editing spans a renderer channel, a control property, a view model, the pick router, the
/// key handler and the save path. A break in any one of them shows up only as a tool that quietly does
/// nothing, so each hop is asserted here rather than left to be noticed.</para>
/// </summary>
public sealed class FaceEditModeTests
{
    private static string? RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? path : null;
    }

    [Fact]
    public void TheModeIsOffAndEmptyToStartWith()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.FaceEditMode);
        Assert.Equal(0, vm.SelectedFaceCount);
        Assert.False(vm.HasFaceSelection);
        Assert.False(vm.HasFaceEdits);
    }

    [Fact]
    public void LeavingTheModeDropsTheSelection()
    {
        // Faces left selected behind a mode you cannot see are how a later Delete removes something the
        // user was not looking at.
        var vm = new MainWindowViewModel { FaceEditMode = true };
        vm.FaceEditMode = false;
        Assert.Equal(0, vm.SelectedFaceCount);
        Assert.Null(vm.SelectedFaceLines);
    }

    [Fact]
    public void TheOperationsRefuseWithNothingSelected()
    {
        var vm = new MainWindowViewModel { FaceEditMode = true };
        Assert.False(vm.DeleteSelectedFacesCommand.CanExecute(null));
        Assert.False(vm.FlipSelectedFacesCommand.CanExecute(null));
        Assert.False(vm.ClearFaceSelectionCommand.CanExecute(null));
    }

    [Fact]
    public void APickInFaceModeGoesToFacesNotMeshes()
    {
        if (RepoFile("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs") is not { } file) return;
        string source = File.ReadAllText(file);

        int route = source.IndexOf("if (FaceEditMode) { SelectFaceFromViewport(", StringComparison.Ordinal);
        int mesh = source.IndexOf("SelectMeshFromViewport(rayOrigin, rayDir, additive, clickScreenPx);", StringComparison.Ordinal);
        Assert.True(route > 0, "face mode must route the pick");
        Assert.True(route < mesh, "the face branch has to run before the mesh pick, and return");
    }

    [Fact]
    public void DeleteAndFlipReachTheFacesBeforeTheMeshDelete()
    {
        // Delete means "delete what is selected". In face mode that is a set of faces; falling through to
        // the mesh delete would remove the whole object instead.
        if (RepoFile("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs") is not { } file) return;
        string source = File.ReadAllText(file);

        int faces = source.IndexOf("vm.FaceEditMode && e.KeyModifiers == KeyModifiers.None", StringComparison.Ordinal);
        int meshDelete = source.IndexOf("vm.DeleteMapContentSelectionCommand.CanExecute(null)", StringComparison.Ordinal);
        Assert.True(faces > 0, "the face key branch is missing");
        Assert.True(faces < meshDelete, "face keys must be handled before the mesh delete");
        Assert.Contains("vm.FlipSelectedFacesCommand", source);
        Assert.Contains("vm.ClearFaceSelectionCommand", source);
    }

    [Fact]
    public void FaceEditsAreWrittenBeforeTheOffsetBasedPassesAndClearedAfterwards()
    {
        // They rewrite index and vertex bytes, so they go first; and replaying them on the next save
        // would apply each twice - a second flip is the identity, a second move travels double.
        if (RepoFile("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs") is not { } file) return;
        string source = File.ReadAllText(file);

        int write = source.IndexOf("MapGeoFaceWriter.TryApply(bytes, map, _faceEdits", StringComparison.Ordinal);
        int layers = source.IndexOf("// 0) M105: layer/controller/backface edits FIRST", StringComparison.Ordinal);
        int clear = source.IndexOf("_faceEdits.Clear();", StringComparison.Ordinal);
        Assert.True(write > 0, "face edits are never written");
        Assert.True(write < layers, "face edits must be written before the offset-based passes");
        Assert.True(clear > write, "the pending list must be cleared after the bytes are written");
    }

    [Fact]
    public void TheHighlightIsWiredThroughToTheRenderer()
    {
        if (RepoFile("src", "ReyEngine.App", "Views", "MainWindow.axaml") is not { } axaml) return;
        if (RepoFile("src", "ReyEngine.App", "Views", "ViewportControl.cs") is not { } control) return;
        if (RepoFile("src", "ReyEngine.Rendering", "ViewportMeshRenderer.cs") is not { } renderer) return;

        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("SelectedFaceLines"));
        Assert.Contains("SelectedFaceLines=\"{Binding SelectedFaceLines}\"", File.ReadAllText(axaml));
        Assert.Contains("IsChecked=\"{Binding FaceEditMode}\"", File.ReadAllText(axaml));
        Assert.Contains("SetSelectedFaceMesh(SelectedFaceLines)", File.ReadAllText(control));
        Assert.Contains("public unsafe void SetSelectedFaceMesh", File.ReadAllText(renderer));
    }
}
