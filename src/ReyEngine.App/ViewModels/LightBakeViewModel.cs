using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.App.Services;
using ReyEngine.Formats.Baking;

namespace ReyEngine.App.ViewModels;

/// <summary>M158: backs the Light Baking window. Holds the bake settings the user tunes, reports live
/// progress, and runs the bake through <see cref="LightBakeService"/>. Every default here is a MEASURED
/// property of Riot's shipped Map12 lightmaps, surfaced so the user can see the real cost before baking.</summary>
public sealed partial class LightBakeViewModel : ObservableObject
{
    private readonly Func<LightBakeInputs?> _gatherInputs;
    private readonly Func<LightBakeService?> _service;
    private readonly Action<LightBakeResult> _onBaked;
    private CancellationTokenSource? _cts;

    public LightBakeViewModel(Func<LightBakeInputs?> gatherInputs, Func<LightBakeService?> service, Action<LightBakeResult> onBaked)
    {
        _gatherInputs = gatherInputs;
        _service = service;
        _onBaked = onBaked;
        RecomputeEstimate();
    }

    // ---- atlas ----
    public IReadOnlyList<int> ResolutionChoices { get; } = new[] { 256, 512, 1024, 2048, 4096 };
    [ObservableProperty] private int _atlasResolution = 2048;
    [ObservableProperty] private double _texelDensity = 0.08;
    [ObservableProperty] private int _padding = 5;
    [ObservableProperty] private int _dilation = 2;
    [ObservableProperty] private bool _compressBc3 = true;
    [ObservableProperty] private bool _generateMips = true;

    // ---- quality ----
    [ObservableProperty] private int _sunSamples = 16;
    [ObservableProperty] private int _pointLightSamples = 4;
    [ObservableProperty] private int _ambientOcclusionSamples = 32;
    [ObservableProperty] private double _ambientOcclusionRadius = 400;
    [ObservableProperty] private double _rayBias = 0.5;

    // ---- lightgrid ----
    [ObservableProperty] private bool _bakeLightGrid = true;
    [ObservableProperty] private int _lightGridWidth = 256;
    [ObservableProperty] private int _lightGridHeight = 256;

    // ---- output ----
    [ObservableProperty] private string _outputRoot = "assets/maps/lightmaps";
    [ObservableProperty] private string _themeToken = "";

    // ---- live state ----
    [ObservableProperty] private bool _isBaking;
    [ObservableProperty] private double _progress;             // 0..1
    [ObservableProperty] private string _stage = "Idle";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _estimate = "";
    [ObservableProperty] private bool _canBake = true;
    [ObservableProperty] private string _blockReason = "";

    public event Action? CloseRequested;

    /// <summary>Turn the bound fields into an immutable BakeSettings.</summary>
    public BakeSettings ToSettings() => new()
    {
        AtlasResolution = AtlasResolution,
        TexelDensity = (float)TexelDensity,
        Padding = Padding,
        Dilation = Dilation,
        CompressBc3 = CompressBc3,
        GenerateMips = GenerateMips,
        SunSamples = SunSamples,
        PointLightSamples = PointLightSamples,
        AmbientOcclusionSamples = AmbientOcclusionSamples,
        AmbientOcclusionRadius = (float)AmbientOcclusionRadius,
        RayBias = (float)RayBias,
        BakeLightGrid = BakeLightGrid,
        LightGridWidth = LightGridWidth,
        LightGridHeight = LightGridHeight,
        LightGridFromMapBounds = true,
        OutputRoot = string.IsNullOrWhiteSpace(OutputRoot) ? "assets/maps/lightmaps" : OutputRoot.Trim(),
        ThemeToken = ThemeToken.Trim(),
    };

    partial void OnAtlasResolutionChanged(int value) => RecomputeEstimate();
    partial void OnCompressBc3Changed(bool value) => RecomputeEstimate();
    partial void OnGenerateMipsChanged(bool value) => RecomputeEstimate();
    partial void OnBakeLightGridChanged(bool value) => RecomputeEstimate();

    private void RecomputeEstimate()
    {
        var s = ToSettings();
        long perAtlas = s.EstimateAtlasBytes();
        var inputs = SafeGatherForEstimate();
        if (inputs is null)
        {
            Estimate = $"{Mb(perAtlas)} per atlas.";
            CanBake = false;
            BlockReason = "Load a map with a lightmap layout, and save the project, before baking.";
            return;
        }

        int atlasCount = LightBaker.EnumerateAtlases(inputs.Map).Count;
        long grid = s.BakeLightGrid ? (long)LightGridWidth * LightGridHeight * 24 + 32 : 0;
        long total = perAtlas * atlasCount + grid;
        Estimate = atlasCount == 0
            ? "This map has no lightmap atlases to bake into."
            : $"{atlasCount} atlas(es) × {Mb(perAtlas)} = {Mb(perAtlas * atlasCount)}" +
              (grid > 0 ? $" + {Mb(grid)} lightgrid" : "") + $"  →  {Mb(total)} total.";
        CanBake = atlasCount > 0;
        BlockReason = atlasCount > 0 ? "" : "No lightmap atlases in this map.";
    }

    private LightBakeInputs? SafeGatherForEstimate()
    {
        try { return _gatherInputs(); } catch { return null; }
    }

    [RelayCommand]
    private async Task BakeAsync()
    {
        if (IsBaking) return;
        var service = _service();
        var inputs = SafeGatherForEstimate();
        if (service is null || inputs is null)
        {
            Status = "Cannot bake: " + (BlockReason.Length > 0 ? BlockReason : "no map or project.");
            return;
        }

        _cts = new CancellationTokenSource();
        IsBaking = true;
        Progress = 0;
        Status = "";
        Stage = "Starting";

        var progress = new Progress<BakeProgress>(p =>
        {
            Stage = p.Texture.Length > 0 ? $"{p.Stage} — {ShortName(p.Texture)}" : p.Stage;
            Progress = p.AtlasCount > 0 ? Math.Clamp(p.AtlasIndex / (double)p.AtlasCount, 0, 1) : 0;
        });

        try
        {
            var settings = ToSettings();
            var result = await Task.Run(() => service.BakeAsync(inputs, settings, progress, _cts.Token), _cts.Token);
            Progress = 1;
            Stage = "Done";
            Status = $"Baked {result.OutputDescription} ({Mb(result.TotalBytes)}).";
            _onBaked(result);
        }
        catch (OperationCanceledException)
        {
            Stage = "Cancelled";
            Status = "Bake cancelled — any atlases already written stay in the override store.";
        }
        catch (Exception ex)
        {
            Stage = "Failed";
            Status = "Bake failed: " + ex.Message;
        }
        finally
        {
            IsBaking = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBaking) _cts?.Cancel();
        else CloseRequested?.Invoke();
    }

    /// <summary>Re-check whether a bake is currently possible (map/project state may have changed since
    /// the window opened). Called when the window is shown.</summary>
    public void Refresh() => RecomputeEstimate();

    private static string ShortName(string path)
    {
        int slash = path.Replace('\\', '/').LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static string Mb(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes} B";
}
