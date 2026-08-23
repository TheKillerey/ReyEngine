using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M566: face editing — select individual triangles of the open map and delete, flip or move them.
///
/// <para>A mode rather than a window: the viewport already has the camera, the pick ray, the gizmo and
/// the undo stack, and a second window would have to grow its own copies of all four.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// While on, a viewport click picks a FACE instead of a mesh.
    ///
    /// <para>Off by default, and it clears its selection on the way out — leaving faces selected behind a
    /// mode you cannot see is how a later Delete removes something the user was not looking at.</para>
    /// </summary>
    [ObservableProperty] private bool _faceEditMode;

    /// <summary>Triangles selected for editing, as indices into the decoded asset's combined list.</summary>
    private readonly HashSet<int> _selectedFaces = new();

    [ObservableProperty] private float[]? _selectedFaceLines;

    public int SelectedFaceCount => _selectedFaces.Count;
    public bool HasFaceSelection => _selectedFaces.Count > 0;

    /// <summary>Face edits made since the map was loaded, replayed onto the file when the map is saved.</summary>
    private readonly List<FaceEdit> _faceEdits = new();

    public bool HasFaceEdits => _faceEdits.Count > 0;

    partial void OnFaceEditModeChanged(bool value)
    {
        if (!value) ClearFaceSelection();
        // Leaving the mode hands the gizmo back to whatever mesh is selected; entering it parks the gizmo
        // until a face is picked, so the arms never point at a target the mode cannot move.
        GizmoPivot = value ? FaceGizmoPivot : _selection.Primary is { } m ? m.Pivot + m.Offset : null;
        _log.Info("Faces", value
            ? "Face mode ON - click a face to select it, Ctrl+click to add or remove. Delete removes, "
              + "F flips, and the gizmo moves the selection."
            : "Face mode off.");
        NotifyFaceState();
    }

    private void NotifyFaceState()
    {
        OnPropertyChanged(nameof(HasFaceGizmoTarget));
        OnPropertyChanged(nameof(FaceGizmoPivot));
        OnPropertyChanged(nameof(SelectedFaceCount));
        OnPropertyChanged(nameof(HasFaceSelection));
        OnPropertyChanged(nameof(HasFaceEdits));
        DeleteSelectedFacesCommand.NotifyCanExecuteChanged();
        FlipSelectedFacesCommand.NotifyCanExecuteChanged();
        ClearFaceSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Picks the face under the ray. Ctrl toggles, a plain click replaces, a miss clears.</summary>
    public void SelectFaceFromViewport(Vector3 rayOrigin, Vector3 rayDir, bool additive)
    {
        if (_currentMap is not { } map) return;
        var hits = RayIndex?.AllHits(rayOrigin, rayDir, CurrentModelSubmeshVisible);
        if (hits is null || hits.Count == 0)
        {
            if (!additive) { ClearFaceSelection(); _log.Info("Faces", "Nothing under the cursor."); }
            return;
        }

        // Nearest first: AllHits does not promise an order, and a face editor that picks the far side of
        // a hill because it happened to be tested first is unusable.
        int triangle = hits.OrderBy(h => h.Distance).First().Triangle;
        if (triangle < 0 || triangle * 3 + 2 >= map.Indices.Length) return;

        if (additive)
        {
            if (!_selectedFaces.Add(triangle)) _selectedFaces.Remove(triangle);
        }
        else { _selectedFaces.Clear(); _selectedFaces.Add(triangle); }

        RebuildFaceSelectionLines();
        _log.Info("Faces", $"{_selectedFaces.Count} face(s) selected.");
    }

    [RelayCommand(CanExecute = nameof(HasFaceSelection))]
    private void ClearFaceSelection()
    {
        if (_selectedFaces.Count == 0) return;
        _selectedFaces.Clear();
        RebuildFaceSelectionLines();
    }

    /// <summary>The selected triangles, drawn as themselves so the highlight sits exactly on the face.</summary>
    private void RebuildFaceSelectionLines()
    {
        if (_currentMap is not { } map || _selectedFaces.Count == 0)
        { SelectedFaceLines = null; NotifyFaceState(); return; }

        var verts = new List<float>(_selectedFaces.Count * 18);
        foreach (int t in _selectedFaces)
        {
            if (t < 0 || t * 3 + 2 >= map.Indices.Length) continue;
            for (int k = 0; k < 3; k++)
            {
                uint v = map.Indices[t * 3 + k];
                if (v * 3 + 2 >= map.Positions.Length) continue;
                verts.Add(map.Positions[v * 3]);
                verts.Add(map.Positions[v * 3 + 1]);
                verts.Add(map.Positions[v * 3 + 2]);
                verts.Add(k == 0 ? 1 : 0);
                verts.Add(k == 1 ? 1 : 0);
                verts.Add(k == 2 ? 1 : 0);
            }
        }
        SelectedFaceLines = verts.Count >= 18 ? verts.ToArray() : null;
        // M567: the gizmo follows the face selection while the mode is on, so the same arms that move a
        // mesh move a set of faces. Leaving it on the mesh would put the handle somewhere unrelated to
        // what a drag is about to affect.
        if (FaceEditMode) GizmoPivot = FaceGizmoPivot;
        NotifyFaceState();
    }

    [RelayCommand(CanExecute = nameof(HasFaceSelection))]
    private void DeleteSelectedFaces() => ApplyFaceOp(FaceOp.Delete, default, "Deleted");

    [RelayCommand(CanExecute = nameof(HasFaceSelection))]
    private void FlipSelectedFaces() => ApplyFaceOp(FaceOp.Flip, default, "Flipped");

    /// <summary>Moves the selection by a delta, as one undo step. For a one-shot nudge, not a drag.</summary>
    public void MoveSelectedFaces(Vector3 delta)
    {
        if (delta == Vector3.Zero) return;
        ApplyFaceOp(FaceOp.Move, delta, "Moved");
    }

    // ---------------------------------------------------------------- M567: gizmo drag
    /// <summary>How far the live drag has moved the faces so far, so each frame can apply the difference.</summary>
    private Vector3 _faceDragApplied;
    private bool _faceDragging;

    /// <summary>Where the face gizmo sits: the centre of the selection's own vertices.</summary>
    public Vector3? FaceGizmoPivot
    {
        get
        {
            if (!FaceEditMode || _currentMap is not { } map || _selectedFaces.Count == 0) return null;
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            bool any = false;
            foreach (int t in _selectedFaces)
            {
                if (t < 0 || t * 3 + 2 >= map.Indices.Length) continue;
                for (int k = 0; k < 3; k++)
                {
                    uint v = map.Indices[t * 3 + k];
                    if (v * 3 + 2 >= map.Positions.Length) continue;
                    var pos = new Vector3(map.Positions[v * 3], map.Positions[v * 3 + 1], map.Positions[v * 3 + 2]);
                    lo = Vector3.Min(lo, pos); hi = Vector3.Max(hi, pos); any = true;
                }
            }
            return any ? (lo + hi) * 0.5f : null;
        }
    }

    public bool HasFaceGizmoTarget => FaceGizmoPivot is not null;

    /// <summary>
    /// Starts a face drag. Mirrors the mesh gizmo: the pointer-move frames mutate silently and the WHOLE
    /// drag becomes one undo step at the end, rather than one step per frame.
    /// </summary>
    public void BeginFaceDrag()
    {
        _faceDragging = true;
        _faceDragApplied = Vector3.Zero;
    }

    /// <summary>
    /// Live-drag to an absolute offset from where the drag started. Absolute rather than incremental for
    /// the same reason the mesh gizmo is: repeated frames would otherwise accumulate, and a frame the
    /// viewport happens to repeat would move the faces twice.
    /// </summary>
    public void DragSelectedFacesTo(Vector3 absoluteOffset)
    {
        if (!_faceDragging || _selectedFaces.Count == 0) return;
        var step = absoluteOffset - _faceDragApplied;
        if (step == Vector3.Zero) return;
        ApplyFaceOpToAsset(_selectedFaces.ToList(), FaceOp.Move, step, forward: true);
        _faceDragApplied = absoluteOffset;
    }

    /// <summary>Closes the drag: one undo step, and one pending edit per face for the save.</summary>
    public void EndFaceDrag()
    {
        if (!_faceDragging) { return; }
        _faceDragging = false;
        if (_faceDragApplied == Vector3.Zero || _selectedFaces.Count == 0) return;

        // The asset already carries the movement - the drag applied it frame by frame - so the command is
        // pushed as ALREADY APPLIED and only records how to undo it.
        var faces = _selectedFaces.ToList();
        var total = _faceDragApplied;
        _faceDragApplied = Vector3.Zero;
        foreach (int t in faces) _faceEdits.Add(new FaceEdit(t, FaceOp.Move, total));
        UndoService.PushApplied(new FaceEditCommand(this, faces, FaceOp.Move, total, alreadyApplied: true));
        NotifyFaceState();
        _log.Info("Faces", $"Moved {faces.Count} face(s) by ({total.X:0.#}, {total.Y:0.#}, {total.Z:0.#}) "
            + "via the gizmo. Save Map Content Edits writes it into the mapgeo.");
    }

    /// <summary>
    /// Records the edit, updates the in-memory asset so the viewport shows it at once, and pushes an undo
    /// step. Nothing reaches the file until the map is saved — the same contract mesh moves already have.
    /// </summary>
    private void ApplyFaceOp(FaceOp op, Vector3 offset, string verb)
    {
        if (_currentMap is not { } map || _selectedFaces.Count == 0) return;
        var faces = _selectedFaces.ToList();
        var command = new FaceEditCommand(this, faces, op, offset);
        command.Execute();
        UndoService.PushApplied(command);
        _log.Info("Faces", $"{verb} {faces.Count} face(s). Undo (Ctrl+Z) reverts it; "
            + "Save Map Content Edits writes it into the mapgeo.");
    }

    /// <summary>Applies an op to the decoded asset in memory, so the viewport reflects it immediately.</summary>
    private void ApplyFaceOpToAsset(IReadOnlyList<int> faces, FaceOp op, Vector3 offset, bool forward)
    {
        if (_currentMap is not { } map) return;
        switch (op)
        {
            case FaceOp.Delete:
                foreach (int t in faces)
                {
                    if (t < 0 || t * 3 + 2 >= map.Indices.Length) continue;
                    if (forward)
                    {
                        _faceUndoIndices[t] = (map.Indices[t * 3], map.Indices[t * 3 + 1], map.Indices[t * 3 + 2]);
                        map.Indices[t * 3 + 1] = map.Indices[t * 3];
                        map.Indices[t * 3 + 2] = map.Indices[t * 3];
                    }
                    else if (_faceUndoIndices.TryGetValue(t, out var original))
                    {
                        map.Indices[t * 3] = original.Item1;
                        map.Indices[t * 3 + 1] = original.Item2;
                        map.Indices[t * 3 + 2] = original.Item3;
                    }
                }
                break;

            case FaceOp.Flip:
                foreach (int t in faces)
                {
                    if (t < 0 || t * 3 + 2 >= map.Indices.Length) continue;
                    (map.Indices[t * 3 + 1], map.Indices[t * 3 + 2]) =
                        (map.Indices[t * 3 + 2], map.Indices[t * 3 + 1]);   // its own inverse
                }
                break;

            case FaceOp.Move:
                // Per VERTEX, not per triangle: a corner shared by two selected faces must move once.
                var touched = new HashSet<uint>();
                foreach (int t in faces)
                {
                    if (t < 0 || t * 3 + 2 >= map.Indices.Length) continue;
                    for (int k = 0; k < 3; k++) touched.Add(map.Indices[t * 3 + k]);
                }
                var step = forward ? offset : -offset;
                foreach (uint v in touched)
                {
                    if (v * 3 + 2 >= map.Positions.Length) continue;
                    map.Positions[v * 3] += step.X;
                    map.Positions[v * 3 + 1] += step.Y;
                    map.Positions[v * 3 + 2] += step.Z;
                }
                break;
        }
        RebuildFaceSelectionLines();
        NotifyMaterialsChanged();   // the DX11 scene is built from these buffers
    }

    /// <summary>What a deleted face looked like, so undo can put it back.</summary>
    private readonly Dictionary<int, (uint, uint, uint)> _faceUndoIndices = new();

    private sealed class FaceEditCommand : ReyEngine.Core.Undo.IEditorCommand
    {
        private readonly MainWindowViewModel _vm;
        private readonly List<int> _faces;
        private readonly FaceOp _op;
        private readonly Vector3 _offset;
        /// <summary>A gizmo drag has already moved the geometry frame by frame, and already recorded its
        /// pending edits. Executing again would move it twice.</summary>
        private bool _skipNextExecute;

        public FaceEditCommand(MainWindowViewModel vm, List<int> faces, FaceOp op, Vector3 offset,
            bool alreadyApplied = false)
        { _vm = vm; _faces = faces; _op = op; _offset = offset; _skipNextExecute = alreadyApplied; }

        public string Name => _op switch
        {
            FaceOp.Delete => "Delete Faces",
            FaceOp.Flip => "Flip Faces",
            _ => "Move Faces",
        };
        public object? Context => null;

        public void Execute()
        {
            // Redo after an undo must run in full; only the first call from a finished drag is skipped.
            if (_skipNextExecute) { _skipNextExecute = false; return; }
            _vm.ApplyFaceOpToAsset(_faces, _op, _offset, forward: true);
            foreach (int t in _faces) _vm._faceEdits.Add(new FaceEdit(t, _op, _offset));
            _vm.NotifyFaceState();
        }

        public void Undo()
        {
            _vm.ApplyFaceOpToAsset(_faces, _op, _offset, forward: false);
            // Drop exactly the entries this command added, from the end - the same face can be edited
            // more than once and only the last of them belongs to this step.
            for (int i = _vm._faceEdits.Count - 1, removed = 0; i >= 0 && removed < _faces.Count; i--)
                if (_vm._faceEdits[i].Op == _op && _faces.Contains(_vm._faceEdits[i].Triangle))
                { _vm._faceEdits.RemoveAt(i); removed++; }
            _vm.NotifyFaceState();
        }

        public bool CanMergeWith(ReyEngine.Core.Undo.IEditorCommand next) => false;
        public void MergeWith(ReyEngine.Core.Undo.IEditorCommand next) => throw new NotSupportedException();
    }
}
