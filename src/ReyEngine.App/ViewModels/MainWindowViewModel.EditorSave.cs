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
        await SaveMapContentEdits();
        if (HasUnsavedPaint) await SavePaintedTextures();
        foreach (var editor in MaterialEditors)
            if (editor.IsDirty && editor.BinEntry is not null && editor.SaveOverride is { } save)
                await save();
        if (MapBinEditor.IsDirty) await MapBinEditor.SaveCommand.ExecuteAsync(null);
        if (BinEditor.ApplyPendingEdits() && BinEditor.IsDirty) await SaveBinToOverride();
        if (ParticleEditor.Document?.IsDirty == true) await SaveParticleOverride();
    }
}
