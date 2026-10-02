using System.Diagnostics;
using System.Numerics;
using System.Text.RegularExpressions;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using Xunit.Abstractions;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M809: releasing the LAST D3D11 device of the process must not unload d3d11.dll.
///
/// <para><c>ShaderPreviewRenderer.Dispose</c> ends by disposing the Silk API object, which frees the library handle Silk loaded.
/// With no other device alive that was the image's last reference: d3d11.dll was unloaded while a thread whose entry point is inside
/// it had been created and not yet started, and the thread then started in the unloaded image - "Attempt to execute non-executable
/// address" in &lt;Unloaded_d3d11.dll&gt;, a native access violation that ends the process (M807: every Map12 thumbnail batch
/// outside a debugger, two of two under cdb with the debug heap off). M807 pinned the library in the thumbnail renderer alone; the
/// pin now lives in <c>ShaderPreviewRenderer.Initialize</c>, so the viewport, the character window, the Particle Editor, the
/// shader preview and the thumbnails all have it.</para>
///
/// <para>The race itself is a native crash that depends on the driver's thread timing, so it is shown with the real Map12 batch under
/// cdb (the M809 harness) and not asserted here: these tests pin where the pin lives and that nothing releases it, and run the
/// create / render / release sequence on a worker thread - the way the thumbnailer does it - where a regression takes the test host
/// down with it. The sequence needs a D3D11 device and the installed game's shader cache; elsewhere it reports SKIPPED, like the
/// other device tests here.</para>
/// </summary>
public sealed class D3D11LibraryPinTests(ITestOutputHelper output)
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const int W = 320, H = 240;

    private static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) return dir.FullName;
        return null;
    }

    private static string? Source(params string[] parts)
    {
        if (RepoRoot() is not { } root) return null;
        string path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static bool IsMapped(string module)
    {
        using var self = Process.GetCurrentProcess();
        return self.Modules.Cast<ProcessModule>().Any(m => string.Equals(m.ModuleName, module, StringComparison.OrdinalIgnoreCase));
    }

    // ================================================================================================ where the pin lives

    [Fact]
    public void Initialize_pins_d3d11_before_the_first_silk_load_and_nothing_ever_releases_the_pin()
    {
        string? renderer = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        string? pin = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.LibraryPin.cs");
        if (renderer is null || pin is null) { output.WriteLine("SKIPPED: sources not found"); return; }

        // the pin comes first in Initialize: nothing of ours can free Silk's handle before the extra reference exists
        int init = renderer.IndexOf("public bool Initialize(out string? error)", StringComparison.Ordinal);
        Assert.True(init > 0, "ShaderPreviewRenderer.Initialize not found");
        int pinned = renderer.IndexOf("PinD3D11Library();", init, StringComparison.Ordinal);
        int api = renderer.IndexOf("SilkD3D11.GetApi(null)", init, StringComparison.Ordinal);
        Assert.True(pinned > init && api > pinned, "d3d11.dll must be pinned before Silk loads it");

        // and the pin is a reference nobody gives back (the method body: the comment above it talks about Dispose)
        int body = pin.IndexOf("public static void PinD3D11Library()", StringComparison.Ordinal);
        Assert.True(body > 0, "PinD3D11Library not found");
        string code = pin.Substring(body);
        Assert.Contains("NativeLibrary.TryLoad(\"d3d11.dll\", out _)", code);
        Assert.DoesNotContain("NativeLibrary.Free", code);
        Assert.DoesNotContain("FreeLibrary", code);
        Assert.DoesNotContain("Dispose", code);
        Assert.Contains("lock (LibraryPinLock)", code);   // a second caller waits for the load instead of racing past it
        Assert.DoesNotContain("NativeLibrary.Free", renderer);   // nor does the renderer free anything of its own accord
        Assert.DoesNotContain("FreeLibrary", renderer);
    }

    [Fact]
    public void The_pin_exists_once_so_no_host_carries_a_copy_of_its_own()
    {
        if (RepoRoot() is not { } root) { output.WriteLine("SKIPPED: repo root not found"); return; }
        var copies = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            if (rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (Regex.IsMatch(File.ReadAllText(file), @"""d3d11\.dll""")) copies.Add(rel);
        }
        Assert.Equal(new[] { Path.Combine("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.LibraryPin.cs") }, copies);

        // the thumbnail renderer used to have one (M807): it creates its device through Initialize like every other host
        string? thumbs = Source("src", "ReyEngine.App", "Services", "MapThumbnailRenderer.cs");
        if (thumbs is null) { output.WriteLine("SKIPPED (thumbnail half): MapThumbnailRenderer.cs not found"); return; }
        Assert.DoesNotContain("NativeLibrary", thumbs);
        Assert.DoesNotContain("PinD3D11Library", thumbs);
        Assert.Contains("created.Initialize(out string? initError)", thumbs);
    }

    [Fact]
    public void The_pin_is_safe_from_any_thread_any_number_of_times_and_leaves_the_image_mapped()
    {
        if (!OperatingSystem.IsWindows()) { output.WriteLine("SKIPPED: d3d11.dll is Windows-only"); return; }

        var failures = new List<Exception>();
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            try { for (int i = 0; i < 200; i++) ShaderPreviewRenderer.PinD3D11Library(); }
            catch (Exception ex) { lock (failures) failures.Add(ex); }
        })).ToList();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        Assert.Empty(failures);
        Assert.True(IsMapped("d3d11.dll"), "d3d11.dll is not mapped after the pin");
    }

    // ================================================================================================ create, render, release

    /// <summary>A visual emitter that does not spawn for a minute: nothing alive, but a particle material registered, so the
    /// renderer has a scene and draws the floor grid - a frame with no game map behind it.</summary>
    private static VfxPlayback DelayedPlayback()
    {
        var emitter = new VfxEmitterDefinition(
            Name: "late", Rate: VfxCurveF.Const(20f), ParticleLifetime: VfxCurveF.Const(1f),
            EmitterLifetime: null, ParticleLinger: 0f, TimeBeforeFirstEmission: 60f, IsSingleParticle: false,
            Disabled: false, BlendMode: 1,
            BirthScale: VfxCurve3.Const(new Vector3(60f)), ScaleOverLife: null,
            BirthColor: VfxCurve4.Const(Vector4.One), ColorOverLife: null,
            BirthVelocity: null, Acceleration: null, BirthRotationalVelocity: null,
            EmitterPosition: VfxCurve3.Const(Vector3.Zero),
            TexturePath: "ASSETS/Test/white.dds", TexDiv: Vector2.One, NumFrames: 1, RandomStartFrame: false,
            IsMeshPrimitive: false);
        var system = new VfxSystemDefinition(0xD809, "PinSystem", "test/m809", new[] { emitter });
        var texture = new TextureImage(1, 1, new byte[] { 255, 255, 255, 255 });
        return new VfxPlayback(new[]
        {
            new VfxPlaybackItem(system, Matrix4x4.Identity, new TextureImage?[] { texture }) { Seed = 809 },
        });
    }

    /// <summary>One renderer's whole life: created, initialized, a frame drawn and read back, released (its Dispose runs on
    /// leaving the method). Returns the number of lit pixels, or -1 when this machine has no device.</summary>
    private static int OneRenderer(ShaderCacheReader cache, out string? error)
    {
        using var renderer = new ShaderPreviewRenderer();
        if (!renderer.Initialize(out error)) return -1;
        renderer.SetMesh(PreviewGeometry.CreateBuiltIn("Sphere"));
        renderer.GroundGrid = true;
        renderer.SetGroundGridLines(ReyEngine.Rendering.GridRenderer.BuildGeometry(20, 100f, out _, out _));

        var eye = new Vector3(0f, 500f, 700f);
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, (float)W / H, 1f, 20000f);
        var mirrored = Matrix4x4.CreateScale(-1f, 1f, 1f) * view;

        var driver = new D3D11MapParticles(renderer, cache);
        driver.SetPlayback(DelayedPlayback());
        for (int i = 0; i < 3; i++) driver.Tick(1f / 30f, mirrored, mirrored * proj, eye, 700f);

        var settings = new PreviewSettings
        {
            SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
            AlphaBlend = true, DepthTest = true, CullBackFaces = true, SortByPipeline = true,
            MirrorX = true, TransposeMatrices = true, Bloom = false, Shadows = false,
            ClearColor = Vector4.Zero,
        };
        var frame = renderer.RenderFrame(W, H, settings, out error, new List<string>());
        driver.StopAll();
        if (frame is null) return 0;
        int lit = 0;
        for (int i = 0; i + 3 < frame.Length; i += 4)
            if (frame[i] > 12 || frame[i + 1] > 12 || frame[i + 2] > 12) lit++;
        return lit;
    }

    [Fact]
    public void Renderers_created_rendered_and_released_in_sequence_on_a_worker_thread_leave_the_image_mapped()
    {
        if (!OperatingSystem.IsWindows()) { output.WriteLine("SKIPPED: D3D11 is Windows-only"); return; }
        if (!Directory.Exists(Final)) { output.WriteLine("SKIPPED: no game install at " + Final); return; }
        var database = new HashSyncService().LoadLocal(_ => { });
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(database), out _);
        if (cache is null) { output.WriteLine("SKIPPED: the game's shader cache did not open"); return; }

        const int Renderers = 4;
        var lit = new List<int>();
        string? skipped = null;
        Exception? failure = null;
        bool mappedAfterLast = false;
        // the way the thumbnailer does it: a below-normal worker that owns its devices one after another, and releases the last one
        var worker = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < Renderers; i++)
                {
                    int n = OneRenderer(cache, out string? error);
                    if (n < 0) { skipped = error; return; }
                    lit.Add(n);
                }
                // the last device is gone: give a driver thread that was still being created every chance to start (and fault)
                Thread.Sleep(2000);
                mappedAfterLast = IsMapped("d3d11.dll");
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "M809PinWorker" };
        worker.Start();
        worker.Join();

        Assert.Null(failure);
        if (skipped is not null) { output.WriteLine("SKIPPED: no D3D11 device here: " + skipped); return; }
        output.WriteLine($"RAN on a real D3D11 device: {Renderers} renderers in sequence, lit pixels per frame [{string.Join(", ", lit)}]; d3d11.dll mapped after the last release: {mappedAfterLast}");
        Assert.Equal(Renderers, lit.Count);
        Assert.All(lit, n => Assert.True(n > 500, $"the floor grid must be on screen ({n} lit pixels)"));
        Assert.True(mappedAfterLast, "d3d11.dll was unloaded when the last device was released");
    }
}
