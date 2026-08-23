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
        Assert.Contains("MeshVerticesRevision++;", vmSource);
        Assert.Contains("NotifyMaterialsChanged();", vmSource);
        Assert.Contains("MeshVerticesRevision=\"{Binding MeshVerticesRevision}\"", File.ReadAllText(axaml));
        Assert.Contains("else if (change.Property == MeshVerticesRevisionProperty)", File.ReadAllText(control));
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
    public void AnInPlaceEditAlsoInvalidatesThePickIndex()
    {
        // The reported bug: a moved face rendered in its new place and still PICKED in its old one, so
        // clicking the thing you just moved selected nothing and clicking where it used to be selected it.
        //
        // M568 invented a GeometryRevision that re-uploaded the buffers and left the ray BVH built from
        // the old positions. MeshVerticesRevision already drove both the vertex upload and that index, so
        // the second counter was half a duplicate of it. There is one now.
        if (RepoFile("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.FaceEdit.cs") is not { } vmFile) return;
        if (RepoFile("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs") is not { } mainFile) return;

        string vmSource = File.ReadAllText(vmFile);
        Assert.Contains("MeshVerticesRevision++;", vmSource);
        Assert.DoesNotContain("GeometryRevision++", vmSource);   // the comment still names it; the code must not
        Assert.Null(typeof(MainWindowViewModel).GetProperty("GeometryRevision"));

        // and that counter is what the ray index checks itself against
        Assert.Contains("_rayIndexRevision == MeshVerticesRevision", File.ReadAllText(mainFile));
    }

    [Fact]
    public void AnInPlaceEditDoesNotThrowAwayTheCamera()
    {
        // M568 routed the re-upload through _meshDirty, which rebuilds every buffer and then sets
        // _needFrame - so the camera jumped back to framing the whole map every time a face moved. The
        // light paths touch the vertex and index buffers and nothing else.
        if (RepoFile("src", "ReyEngine.App", "Views", "ViewportControl.cs") is not { } file) return;
        string source = File.ReadAllText(file);

        // the change HANDLER, not the styled-property declaration that shares the name
        int at = source.IndexOf("else if (change.Property == MeshVerticesRevisionProperty)", StringComparison.Ordinal);
        Assert.True(at > 0, "the revision has to invalidate something");
        string branch = source.Substring(at, Math.Min(900, source.Length - at));
        Assert.Contains("_verticesDirty = true", branch);
        Assert.Contains("_indicesDirty = true", branch);
        Assert.DoesNotContain("_meshDirty", branch);
        Assert.Contains("_meshRenderer.UpdateIndices(", source);
    }

    [Fact]
    public void BothOverlaysExistInTheD3D11ViewportToo()
    {
        // They were built for the GL renderer only, so in DX11 mode - which is where the reporter works -
        // the navgrid and the face highlight drew nothing at all.
        if (RepoFile("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs") is not { } renderer) return;
        if (RepoFile("src", "ReyEngine.App", "Views", "MainWindow.axaml.cs") is not { } host) return;

        string source = File.ReadAllText(renderer);
        Assert.Contains("public void SetNavGridCells", source);
        Assert.Contains("public void SetSelectedFaces", source);
        Assert.Contains("DrawNavGridAndFaces(view, proj)", source);
        // depth OFF, like the GL side - a cell buried in terrain still has to be visible
        Assert.Contains("_ctx.OMSetDepthStencilState(_overlayDepthNoTest, 0);", source);

        string feed = File.ReadAllText(host);
        Assert.Contains("SetNavGridCells(vm.BushCellLines, vm.BushCellLayers)", feed);
        Assert.Contains("SetSelectedFaces(vm.SelectedFaceLines)", feed);
    }

    [Fact]
    public void ExtrudeAndInsetAreWrittenBeforeAnythingThatUsesAByteOffset()
    {
        // They GROW the index buffer, so every submesh offset after the insertion moves. Anything that
        // located a byte offset earlier would then be reading the wrong place - and the length-preserving
        // face edits address triangles by their ORIGINAL index, which only holds while nothing has been
        // inserted yet. So: face edits, then grows, then the offset-based passes.
        if (RepoFile("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs") is not { } file) return;
        string source = File.ReadAllText(file);

        int faces = source.IndexOf("MapGeoFaceWriter.TryApply(bytes, map, _faceEdits", StringComparison.Ordinal);
        int grows = source.IndexOf("MapGeoFaceGrower.TryApply(bytes, map, _faceGrows", StringComparison.Ordinal);
        int layers = source.IndexOf("// 0) M105: layer/controller/backface edits FIRST", StringComparison.Ordinal);
        Assert.True(grows > 0, "extrude/inset are never written");
        Assert.True(faces < grows, "length-preserving edits must run before anything is inserted");
        Assert.True(grows < layers, "grows must run before the offset-based passes");
        Assert.Contains("_faceGrows.Clear();", source);
    }

    [Fact]
    public void TheGrowCommandsExistAndRefuseWithNothingSelected()
    {
        var vm = new MainWindowViewModel { FaceEditMode = true };
        Assert.False(vm.ExtrudeSelectedFacesCommand.CanExecute(null));
        Assert.False(vm.InsetSelectedFacesCommand.CanExecute(null));
        Assert.False(vm.HasFaceGrows);
    }

    [Fact]
    public void TheAmountsParseInvariantlySoAGermanLocaleDoesNotBreakThem()
    {
        // The machine this runs on uses a comma decimal separator, so a culture-sensitive parse would read
        // "0.25" as 25 and inset the face into nothing.
        var vm = new MainWindowViewModel { ExtrudeAmount = "12.5", InsetAmount = "0.25" };
        Assert.Equal("12.5", vm.ExtrudeAmount);
        Assert.Equal("0.25", vm.InsetAmount);
        if (RepoFile("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.FaceEdit.cs") is not { } file) return;
        Assert.Contains("CultureInfo.InvariantCulture", File.ReadAllText(file));
    }

    [Fact]
    public void TheSaveGateIsNotNarrowerThanTheGuardInsideTheSave()
    {
        // How face edits came to be silently unsaveable. The condition deciding whether to CALL the save
        // listed moves, deletions and added meshes; the guard INSIDE it also knew about face edits and
        // grows. So face work passed a check it never reached and saving did nothing at all.
        //
        // Both now ask one property. This asserts the duplicate condition is gone rather than just that
        // today's version is right - a second copy is what drifted.
        if (RepoFile("src", "ReyEngine.App", "ViewModels", "MainWindowViewModel.cs") is not { } file) return;
        string source = File.ReadAllText(file);

        Assert.NotNull(typeof(MainWindowViewModel).GetProperty("HasPendingMapGeoWork"));
        Assert.Contains("if (HasPendingMapGeoWork) await SaveMeshMoves();", source);
        Assert.Contains("if (HasPendingMapGeoWork)", source);

        // the hand-written condition must not survive anywhere
        Assert.DoesNotContain("HasMapMoves || MapContent.AllMapPieces.Any(p => p.IsRemoved)", source);

        // and it has to cover everything the inner guard covers
        int guard = source.IndexOf("if (!hasFaces && !hasGrows", StringComparison.Ordinal);
        Assert.True(guard > 0, "the inner guard changed shape; check the gate still matches it");
    }

    [Fact]
    public void APendingFaceEditIsEnoughToMakeTheSaveRun()
    {
        // The property itself, not the source text: with nothing pending there is nothing to save.
        var vm = new MainWindowViewModel();
        Assert.False(vm.HasPendingMapGeoWork);
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
