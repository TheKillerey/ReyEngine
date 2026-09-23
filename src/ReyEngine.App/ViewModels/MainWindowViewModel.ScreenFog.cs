using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Assets;
using ReyEngine.Core.Projects;
using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M760: the map's SCREEN fog - the depth and height fog of <c>PostEffectOptions</c> (see
/// <see cref="MapPostFog"/>). A second fog model beside the sun's environment fog: gamma/postfog.ps runs over
/// the finished frame, so it fogs everything by distance from the camera and by world height, sky excepted.
///
/// <para>No shipped map carries the component, so on Live both fogs read their (off) defaults. Every edit
/// re-renders both viewports at once; "Save screen fog to map" writes the component into the map's
/// materials.bin (creating it when the map has none) and reads it back before keeping it.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>What the viewports draw. Null when every fog is off.</summary>
    [ObservableProperty] private MapPostFog? _currentPostFog;

    /// <summary>True when the open map's bin already declares the post-effects component.</summary>
    [ObservableProperty] private bool _mapHasPostFogComponent;

    [ObservableProperty] private bool _depthFogEnabled;
    [ObservableProperty] private double _depthFogStart = 5000;
    [ObservableProperty] private double _depthFogEnd = 8000;
    [ObservableProperty] private double _depthFogMaxIntensity = 1;
    [ObservableProperty] private bool _heightFogEnabled;
    [ObservableProperty] private double _heightFogStart = 300;
    [ObservableProperty] private double _heightFogEnd = -100;
    [ObservableProperty] private double _heightFogMaxIntensity = 1;

    // Colours are held as the bin's floats, not as 8-bit picker colours: a map that authors 0.447 must not
    // come back as 114/255 on a save that did not touch it.
    private Vector4 _depthFogColor = MapPostFog.Defaults.DepthFog.Color;
    private Vector4 _heightFogColor = MapPostFog.Defaults.HeightFog.Color;
    private MapPostFog _basePostFog = MapPostFog.Defaults;
    private bool _suppressPostFogRebuild;

    public Avalonia.Media.Color DepthFogColorPick
    {
        get => Col(_depthFogColor.X, _depthFogColor.Y, _depthFogColor.Z);
        set { _depthFogColor = new Vector4(value.R / 255f, value.G / 255f, value.B / 255f, 1f); OnPropertyChanged(); RebuildPostFog(); }
    }

    public Avalonia.Media.Color HeightFogColorPick
    {
        get => Col(_heightFogColor.X, _heightFogColor.Y, _heightFogColor.Z);
        set { _heightFogColor = new Vector4(value.R / 255f, value.G / 255f, value.B / 255f, 1f); OnPropertyChanged(); RebuildPostFog(); }
    }

    /// <summary>One line for the card: what the map ships and what is drawn now.</summary>
    public string ScreenFogStatus =>
        (MapHasPostFogComponent ? "This map declares the post-effects component. " : "This map has no post-effects component (no Live map does); saving adds one. ")
        + (CurrentPostFog is null ? "Nothing is drawn." : "Drawing: "
            + string.Join(" + ", new[] { DepthFogEnabled ? "depth fog" : null, HeightFogEnabled ? "height fog" : null }.Where(x => x is not null)) + ".");

    partial void OnDepthFogEnabledChanged(bool value) => RebuildPostFog();
    partial void OnDepthFogStartChanged(double value) => RebuildPostFog();
    partial void OnDepthFogEndChanged(double value) => RebuildPostFog();
    partial void OnDepthFogMaxIntensityChanged(double value) => RebuildPostFog();
    partial void OnHeightFogEnabledChanged(bool value) => RebuildPostFog();
    partial void OnHeightFogStartChanged(double value) => RebuildPostFog();
    partial void OnHeightFogEndChanged(double value) => RebuildPostFog();
    partial void OnHeightFogMaxIntensityChanged(double value) => RebuildPostFog();

    /// <summary>The panel values as the record the writer and the viewports take.</summary>
    private MapPostFog PanelPostFog() => new(
        new MapScreenFog(DepthFogEnabled, _depthFogColor, (float)DepthFogStart, (float)DepthFogEnd, (float)DepthFogMaxIntensity),
        new MapScreenFog(HeightFogEnabled, _heightFogColor, (float)HeightFogStart, (float)HeightFogEnd, (float)HeightFogMaxIntensity));

    private void RebuildPostFog()
    {
        if (_suppressPostFogRebuild) return;
        var fog = PanelPostFog();
        CurrentPostFog = fog.DrawsAnything ? fog : null;
        OnPropertyChanged(nameof(ScreenFogStatus));
    }

    /// <summary>Put the panel on <paramref name="fog"/> (the map's, or the defaults).</summary>
    private void ApplyPostFog(MapPostFog? fog, bool declared)
    {
        _basePostFog = fog ?? MapPostFog.Defaults;
        MapHasPostFogComponent = declared;
        _suppressPostFogRebuild = true;
        try
        {
            var d = _basePostFog.DepthFog; var h = _basePostFog.HeightFog;
            DepthFogEnabled = d.Enabled; _depthFogColor = d.Color; DepthFogStart = d.Start; DepthFogEnd = d.End; DepthFogMaxIntensity = d.MaxIntensity;
            HeightFogEnabled = h.Enabled; _heightFogColor = h.Color; HeightFogStart = h.Start; HeightFogEnd = h.End; HeightFogMaxIntensity = h.MaxIntensity;
        }
        finally { _suppressPostFogRebuild = false; }
        OnPropertyChanged(nameof(DepthFogColorPick));
        OnPropertyChanged(nameof(HeightFogColorPick));
        RebuildPostFog();
    }

    /// <summary>Read the open map's screen fog. Called on map load, next to the sun.</summary>
    private void LoadScreenFog()
    {
        MapPostFog? fog = null;
        try
        {
            if (_currentMapEntry is not null && TryResolveMaterialsBin(_currentMapEntry.Path, out var binEntry))
                fog = MapPostFog.Extract(ReadAsset(binEntry.PathHash));
        }
        catch (Exception ex) { _log.Warn("Map", "Could not read the map's screen fog: " + ex.Message); }
        ApplyPostFog(fog, fog is not null);
        if (fog is not null)
            _log.Info("Map", $"PostEffectOptions: depth fog {(fog.DepthFog.Enabled ? "on" : "off")} {fog.DepthFog.Start:0}..{fog.DepthFog.End:0}, "
                + $"height fog {(fog.HeightFog.Enabled ? "on" : "off")} {fog.HeightFog.Start:0}..{fog.HeightFog.End:0}.");
    }

    [RelayCommand]
    private void ResetScreenFog() => ApplyPostFog(_basePostFog, MapHasPostFogComponent);

    /// <summary>Write the panel's screen fog into the map's materials.bin, validated and read back first -
    /// the same contract as <see cref="SaveSunToMap"/>.</summary>
    [RelayCommand]
    private async Task SaveScreenFogToMap()
    {
        if (_currentMapEntry is not { } entry)
        { _log.Warn("Lighting", "No map is open, so there is nowhere to save the screen fog."); return; }
        if (!TryResolveMaterialsBin(entry.Path, out var binEntry))
        { _log.Error("Lighting", "No materials.bin was found alongside this mapgeo."); return; }
        if (!GuardEditable(binEntry)) return;
        if (!await EnsureProjectSavedAsync()) return;

        try
        {
            var fog = PanelPostFog();
            byte[] source = ReadAsset(binEntry.PathHash);
            var (bytes, detail) = await Task.Run(() =>
            {
                var b = MapPostFog.Write(source, fog, out var r);
                return (b, r);
            });
            if (bytes is null) { _log.Error("Lighting", "Screen fog could not be written: " + detail); return; }
            if (ReferenceEquals(bytes, source)) { _log.Info("Lighting", "Screen fog: " + detail + "."); return; }

            var issues = await Task.Run(() => Formats.Meta.ModShapeValidator.ValidateBin(
                Formats.Meta.SafeBinTree.Parse(bytes), bytes, ResolveBinName));
            if (issues.Count > 0)
            {
                foreach (var i in issues.Take(5)) _log.Error("Lighting", $"[{i.Category}] {i.ObjectName}: {i.Detail}");
                _log.Error("Lighting", $"{issues.Count} shape issue(s) - not saved."); return;
            }
            var back = MapPostFog.Extract(bytes);
            if (back is null || back != fog)
            { _log.Error("Lighting", "The rewritten bin did not read back with the saved screen fog - not saved."); return; }

            string savedTo;
            if (TryWriteToProjectFile(binEntry, bytes, out var projectFile)) savedTo = projectFile;
            else
            {
                savedTo = ProjectWorkspace.StoreOverrideBytes(Project, binEntry.PathHash, bytes, ".bin");
                _overrides.Set(new ProjectAssetOverride
                {
                    PathHash = binEntry.PathHash,
                    ResolvedPath = binEntry.IsResolved ? binEntry.Path : null,
                    OverrideFile = savedTo,
                    AddedUtc = DateTime.UtcNow.ToString("o"),
                });
                _overrides.SaveTo(Project);
            }
            SetNodeStatus(binEntry.PathHash, AssetStatus.Modified);
            Project.IsDirty = true;
            if (Project.ProjectFilePath is not null) ReyProjectService.Save(Project, Project.ProjectFilePath);
            UpdateTitle();

            _basePostFog = fog;
            MapHasPostFogComponent = true;
            OnPropertyChanged(nameof(ScreenFogStatus));
            RefreshMapGraphicsFeatures();   // the component is a MapGraphicsFeature; its row appears
            _log.Success("Lighting", $"Saved the screen fog to the map ({detail}). Saved to {savedTo}.");
        }
        catch (Exception ex) { _log.Error("Lighting", "Screen fog could not be saved: " + ex.Message); }
    }
}
