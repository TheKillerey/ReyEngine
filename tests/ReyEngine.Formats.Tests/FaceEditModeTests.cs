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

        int route = source.IndexOf("if (FaceEditMode)", StringComparison.Ordinal);
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
    public void TheGizmoDragsFacesBeforeMeshesAndPlacements()
    {
        // In face mode a mesh is usually still selected underneath, so the face branch has to come first
        // or the gizmo drags the whole object instead of the faces the user picked.
        if (RepoFile("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs") is not { } file) return;
        string source = File.ReadAllText(file);

        int faceStart = source.IndexOf("vm.FaceEditMode && vm.HasFaceGizmoTarget", StringComparison.Ordinal);
        int meshStart = source.IndexOf("if (vm.SelectedMapMesh is { } mesh)", StringComparison.Ordinal);
        Assert.True(faceStart > 0 && faceStart < meshStart, "the face drag branch must precede the mesh one");

        Assert.Contains("gvm.DragSelectedFacesTo(target)", source);
        Assert.Contains("EndFaceDrag()", source);
        int faceMove = source.IndexOf("gvm.DragSelectedFacesTo(target)", StringComparison.Ordinal);
        int meshMove = source.IndexOf("gvm.DragSelectedMeshTo(target)", StringComparison.Ordinal);
        Assert.True(faceMove < meshMove, "the face branch must be checked before the mesh one on move too");
    }

    [Fact]
    public void ADragIsOneUndoStepAndIsNotAppliedTwice()
    {
        // Two traps in one place. The pointer-move frames already moved the geometry, so the command is
        // pushed ALREADY APPLIED and must skip its first Execute - otherwise finishing a drag moves the
        // faces a second time. But a REDO after an undo has to run in full.
        if (RepoFile("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.FaceEdit.cs") is not { } file) return;
        string source = File.ReadAllText(file);

        Assert.Contains("alreadyApplied: true", source);
        Assert.Contains("if (_skipNextExecute) { _skipNextExecute = false; return; }", source);
        // and the live drag applies the DIFFERENCE, so a repeated frame cannot accumulate
        Assert.Contains("var step = absoluteOffset - _faceDragApplied;", source);
    }

    [Fact]
    public void TheGizmoFollowsTheFaceSelection()
    {
        if (RepoFile("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.FaceEdit.cs") is not { } file) return;
        string source = File.ReadAllText(file);
        Assert.Contains("if (FaceEditMode) GizmoPivot = FaceGizmoPivot;", source);
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("HasFaceGizmoTarget"));
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("FaceGizmoPivot"));

        // with nothing selected there is no target, so the arms never point at something unmovable
        var vm = new MainWindowViewModel { FaceEditMode = true };
        Assert.False(vm.HasFaceGizmoTarget);
        Assert.Null(vm.FaceGizmoPivot);
    }

    [Fact]
    public void AnInPlaceEditTellsBothViewportsToReupload()
    {
        // The bug behind "I move a face and it changed nothing". The GL viewport re-uploads only when its
        // Mesh PROPERTY changes, and a face edit rewrites the arrays behind the same object - so without
        // an explicit revision the screen keeps showing whatever was uploaded at load. The D3D11 side
        // rebuilds from a materials bump. Both are needed; either one alone leaves a viewport stale.
        if (RepoFile("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.FaceEdit.cs") is not { } vmFile) return;
        if (RepoFile("src", "ReyEngine.App", "Views", "ViewportControl.cs") is not { } control) return;
        if (RepoFile("src", "ReyEngine.App", "Views", "MainWindow.axaml") is not { } axaml) return;

        string vmSource = File.ReadAllText(vmFile);
        Assert.Contains("GeometryRevision++;", vmSource);
        Assert.Contains("NotifyMaterialsChanged();", vmSource);
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("GeometryRevision"));
        Assert.Contains("GeometryRevision=\"{Binding GeometryRevision}\"", File.ReadAllText(axaml));
        Assert.Contains("change.Property == GeometryRevisionProperty", File.ReadAllText(control));
        Assert.Contains("_meshDirty = true", File.ReadAllText(control));
    }

    [Fact]
    public void DoubleClickTakesTheWholeConnectedPiece()
    {
        // A quad is two triangles, so a single click on a flat surface selects half of it and moving that
        // tears the quad apart. The count comes from the PRESS because the release event does not carry
        // it, while the pick has to happen on release so a camera drag is not read as a click.
        if (RepoFile("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs") is not { } vm) return;
        if (RepoFile("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs") is not { } window) return;

        Assert.Contains("if (doubleClick) SelectLinkedFacesFromViewport", File.ReadAllText(vm));
        string source = File.ReadAllText(window);
        Assert.Contains("_pressClickCount = e.ClickCount;", source);
        Assert.Contains("doubleClick: _pressClickCount >= 2", source);
    }

    [Fact]
    public void ALinkedSelectionIsBoundedAndSaysSoWhenItStops()
    {
        // A map's ground is one connected sheet of several hundred thousand triangles. Selecting all of it
        // from a double-click is not a useful outcome, and stopping silently is worse than stopping loudly.
        Assert.InRange(MainWindowViewModel.LinkedFaceLimit, 1_000, 100_000);
        if (RepoFile("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.FaceEdit.cs") is not { } file) return;
        string source = File.ReadAllText(file);
        Assert.Contains("stopped at the", source);
        // and the flood stays inside the clicked group rather than crossing the whole map
        Assert.Contains("int firstTriangle = owner.StartIndex / 3;", source);
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
