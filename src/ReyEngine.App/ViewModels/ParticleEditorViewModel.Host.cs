using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.App.ViewModels;

/// <summary>M755: a character to stand in the particle preview as the effect's host - what the character
/// window currently shows, handed over whole.</summary>
public sealed record ParticleHostModel(
    string Name,
    MeshAsset Mesh,
    SkeletonAsset? Skeleton,
    IReadOnlyList<TextureImage?>? Textures,
    IReadOnlyList<ReyEngine.Rendering.ViewportMeshRenderer.SubmeshMaterial>? Materials,
    AnimationClip? Clip);

/// <summary>
/// M755: the Character host. About 1,190 shipped emitters are born on "the character this effect is
/// attached to" and name no mesh of their own (Akali's W cast, Aatrox's dash, the ward pads), so a preview
/// with nobody in it can only emit them from a point. The host is borrowed from the character window -
/// the skin already loaded there, its textures and its current clip - rather than picked again here: the
/// window owns the WADs, the skin bins and the clip list, and this view model has none of those.
///
/// <para>The host's clip runs on this editor's own clock, at its Speed and under its Pause, so the body
/// and the particles leaving it stay on one timeline. The viewport runs the effects on the clip's clock
/// while a clip is bound (M726), which is what makes that hold.</para>
/// </summary>
public sealed partial class ParticleEditorViewModel
{
    /// <summary>What the character window is showing, or null when it shows nothing usable.</summary>
    public Func<ParticleHostModel?>? ResolveHost;

    [ObservableProperty] private MeshAsset? _hostMesh;
    [ObservableProperty] private SkeletonAsset? _hostSkeleton;
    [ObservableProperty] private IReadOnlyList<TextureImage?>? _hostTextures;
    [ObservableProperty] private IReadOnlyList<ReyEngine.Rendering.ViewportMeshRenderer.SubmeshMaterial>? _hostMaterials;
    [ObservableProperty] private AnimationClip? _hostClip;
    [ObservableProperty] private double _hostTime;
    [ObservableProperty] private string _hostName = "";

    public bool HasHost => HostMesh is not null;

    public string HostStatus => HostMesh is null
        ? "No host"
        : $"{HostName}" + (HostClip is { } c ? $" - {c.Name}" : " - bind pose");

    /// <summary>How many emitters of the selected system are born on the host.</summary>
    public int HostEmitterCount =>
        SelectedSystem is { } node && _defs.TryGetValue(node.Entry.PathHash, out var def)
            ? def.Emitters.Count(e => e.EmissionSurface?.NeedsHost == true)
            : 0;

    public string HostHint => HostEmitterCount switch
    {
        0 => "No emitter in this system is born on the character; a host only stands in the view.",
        1 => "1 emitter here is born on the character it is attached to" + (HasHost ? " - it emits from the host." : ". Pick a host to see it."),
        var n => $"{n} emitters here are born on the character they are attached to" + (HasHost ? " - they emit from the host." : ". Pick a host to see them."),
    };

    partial void OnHostMeshChanged(MeshAsset? value)
    {
        OnPropertyChanged(nameof(HasHost));
        OnPropertyChanged(nameof(HostStatus));
        OnPropertyChanged(nameof(HostHint));
        RefreshEmissionNotes();
    }

    partial void OnHostClipChanged(AnimationClip? value) => OnPropertyChanged(nameof(HostStatus));

    /// <summary>The selected system changed or was re-extracted: its host count may have.</summary>
    private void NotifyHostCounts()
    {
        OnPropertyChanged(nameof(HostEmitterCount));
        OnPropertyChanged(nameof(HostHint));
    }

    private void RefreshEmissionNotes()
    {
        foreach (var c in Cards) c.NotifyEmissionNote();
    }

    [RelayCommand]
    private void UseCharacterWindowModel()
    {
        if (ResolveHost?.Invoke() is not { } host)
        {
            Error?.Invoke("Open a character in the character window first - the host is whatever it shows.");
            return;
        }
        HostName = host.Name;
        HostSkeleton = host.Skeleton;
        HostTextures = host.Textures;
        HostMaterials = host.Materials;
        HostClip = host.Clip;
        HostTime = 0;
        HostMesh = host.Mesh;   // last: the notes read the name
        StartHostClock();
        Info?.Invoke($"Host: {HostStatus}.");
    }

    [RelayCommand]
    private void ClearHost()
    {
        _hostTimer?.Stop();
        HostMesh = null;
        HostSkeleton = null;
        HostTextures = null;
        HostMaterials = null;
        HostClip = null;
        HostTime = 0;
        HostName = "";
    }

    private DispatcherTimer? _hostTimer;
    private readonly Stopwatch _hostWatch = new();

    private void StartHostClock()
    {
        if (HostClip is null) { _hostTimer?.Stop(); return; }
        if (_hostTimer is null)
        {
            _hostTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _hostTimer.Tick += (_, _) => AdvanceHost(_hostWatch.Elapsed.TotalSeconds);
        }
        _hostWatch.Restart();
        _hostTimer.Start();
    }

    /// <summary>One tick of the host clock: the wall time since the last one, at the editor's Speed,
    /// nothing while paused, wrapped at the clip's end.</summary>
    internal void AdvanceHost(double elapsedSeconds)
    {
        _hostWatch.Restart();
        if (HostClip is not { Duration: > 1e-3f } clip || Paused) return;
        HostTime = (HostTime + elapsedSeconds * Speed) % clip.Duration;
    }
}
