using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ReyEngine.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private DispatcherTimer? _regularAutoSaveTimer;
    public void StopEditorAutoSave()
    {
        _regularAutoSaveTimer?.Stop();
        _autoSaveTimer?.Stop();
    }

    private void StartRegularAutoSave()
    {
        _regularAutoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _regularAutoSaveTimer.Tick += OnAutoSaveTick;
        _regularAutoSaveTimer.Start();
    }

    // Manual save and both autosave clocks flush through the existing document save/apply paths.
    private async Task SavePendingEditorEdits()
    {
        EndFaceDrag();
        await SaveMapContentEdits();
        if (HasFaceEdits || HasFaceGrows)
            throw new InvalidOperationException("Mesh geometry could not be saved; edits remain pending.");
        if (HasUnsavedPaint) await SavePaintedTextures();
        foreach (var editor in MaterialEditors)
            if (editor.IsDirty && editor.BinEntry is not null && editor.SaveOverride is { } save)
                await save();
        if (MapBinEditor.IsDirty) await MapBinEditor.SaveCommand.ExecuteAsync(null);
        if (BinEditor.ApplyPendingEdits() && BinEditor.IsDirty) await SaveBinToOverride();
        if (ParticleEditor.Document?.IsDirty == true) await SaveParticleOverride();
        // Map tabs keep their own authoritative geometry. Ctrl+S also flushes inactive face edits.
        var otherMaps = Documents.Select(d => d.Scene).OfType<MapScene>()
            .Where(s => !ReferenceEquals(s.Map, _currentMap) && (s.Faces is { Dirty: true } || s.Faces?.Grows.Count > 0)).ToArray();
        if (otherMaps.Length > 0 && CaptureMapScene() is { } active)
        {
            var selectedFaces = _selectedFaces.ToArray();
            try
            {
                foreach (var scene in otherMaps)
                {
                    RestoreMapScene(scene);
                    await SaveMeshMoves();
                    if (HasFaceEdits || HasFaceGrows)
                        throw new InvalidOperationException("Mesh geometry in another map tab could not be saved; edits remain pending.");
                }
            }
            finally
            {
                RestoreMapScene(active);
                _selectedFaces.UnionWith(selectedFaces);
                RebuildFaceSelectionLines();
            }
        }
    }
}
