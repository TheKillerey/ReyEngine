using System.Diagnostics;
using System.Numerics;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Formats.Lighting;
using ReyEngine.Formats.MapGeo;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ReyEngine.App.Services;

/// <summary>M807: one map thumbnail to draw - everything the draw needs, with no view model in it. Every delegate is called on
/// the worker thread, so each must be safe to call from there (the asset readers are: a WAD reads under its own lock).</summary>
public sealed class MapThumbnailJob
{
    /// <summary>What the picture is cached under (<see cref="MapThumbnailKey"/>).</summary>
    public required string Key { get; init; }
    /// <summary>The mapgeo's virtual path, <c>data/maps/mapgeometry/map22/anniversary.mapgeo</c>.</summary>
    public required string MapGeoPath { get; init; }
    public required ulong MapGeoHash { get; init; }
    /// <summary>The mapgeo's materials bin, as the editor resolves it for this mapgeo (it tolerates renamed copies).</summary>
    public required string MaterialsBinPath { get; init; }
    public required ulong MaterialsBinHash { get; init; }
    /// <summary>Other bins in the mapgeo's folder whose visibility controllers the map may name, read before the materials
    /// bin (which is added last and wins on a repeated controller). May be empty.</summary>
    public IReadOnlyList<ulong> ControllerBins { get; init; } = Array.Empty<ulong>();
    /// <summary>The game's <c>DATA/FINAL</c> folder (it holds <c>ShaderCache.dx11.wad.client</c>).</summary>
    public required string FinalDirectory { get; init; }

    /// <summary>An asset's bytes by path hash; null when it is missing or cannot be read. Never throws.</summary>
    public required Func<ulong, byte[]?> Read { get; init; }
    /// <summary>Is the asset there at all? (Cheaper than reading it.)</summary>
    public required Func<ulong, bool> Has { get; init; }
    public required IHashResolver Hashes { get; init; }
    public required Func<uint, string?> BinName { get; init; }
    public required Func<ulong, string?> WadPath { get; init; }

    /// <summary>Polled between stages: true once the result would be thrown away (the content moved on, the app is closing).
    /// The job then stops at the next stage boundary. Set by the service when it queues the job.</summary>
    public Func<bool>? IsCancelled { get; set; }

    /// <summary>True while the editor is busy with something a background job must not compete with (a map loading, the D3D11
    /// viewport building its scene). Polled at every stage boundary and before every texture decode; the job WAITS while it is
    /// true - it is not cancelled - and goes on when it clears. Set by the service when it queues the job.</summary>
    public Func<bool>? ShouldPause { get; set; }

    /// <summary>Game Depth (<see cref="Dx11SceneBuilder.EmulateClientDepthRules"/>) as it was when the cache key was computed.
    /// The draw uses THIS, not the static, so a toggle between describing the tile and drawing the map cannot file a picture
    /// under a key it was not drawn for.</summary>
    public bool ClientDepthRules { get; init; }

    /// <summary>How many reads or lookups THREW (a mount rebuilt under the job, a collection changed while it was enumerated) -
    /// not the ones that found nothing. Such a read answers "missing", so a picture drawn after one may be degraded: the
    /// service does not cache it. Null counts nothing.</summary>
    public Func<int>? ReadFaults { get; init; }

    private long _bytesRead;
    /// <summary>What the job has read so far, in bytes - the measure of how heavy it was (a Summoner's Rift reads gigabytes, a
    /// TFT board tens of megabytes), which decides how hard the renderer works to give the memory back.</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    /// <summary><see cref="Read"/>, counted. The renderer reads everything through this.</summary>
    public byte[]? ReadCounted(ulong hash)
    {
        var bytes = Read(hash);
        if (bytes is not null) Interlocked.Add(ref _bytesRead, bytes.Length);
        return bytes;
    }
}

/// <summary>M807: what a thumbnail job came to. Exactly one of <see cref="Png"/> and <see cref="Failure"/> is set.</summary>
public sealed record MapThumbnailOutcome
{
    /// <summary>The finished picture, <see cref="MapThumbnailKey.ThumbWidth"/> by <see cref="MapThumbnailKey.ThumbHeight"/>.</summary>
    public byte[]? Png { get; init; }
    public string? Failure { get; init; }
    /// <summary>The failure is a fact about this map (it decodes to nothing, resolves no material, draws nothing) and will
    /// recur - so it is remembered on disk. False for anything that may not recur: a cancelled job, an unreadable asset, a
    /// device that would not start, an exception.</summary>
    public bool Permanent { get; init; }
    /// <summary>The job stopped because <see cref="MapThumbnailJob.IsCancelled"/> said so. Nothing is recorded.</summary>
    public bool Cancelled { get; init; }
    /// <summary>The D3D11 device could not be created: no thumbnails this session.</summary>
    public bool DeviceFailed { get; init; }
    /// <summary>Not drawn: the picture was found already cached when the job came to be drawn (another tile of the same map
    /// finished first), and this is it. Set by the service, never by the renderer.</summary>
    public bool FromCache { get; init; }
    /// <summary>What the job read, in bytes (<see cref="MapThumbnailJob.BytesRead"/>).</summary>
    public long BytesRead { get; init; }

    public double TotalMs { get; init; }
    public string Timings { get; init; } = "";
    public int Slices { get; init; }
    public int HiddenSlices { get; init; }
    public int PropMeshes { get; init; }
    public int PropInstances { get; init; }
    public int PropsDrawn { get; init; }
    /// <summary>The share (0..1) of the rendered frame that is not the clear colour.</summary>
    public double NonClearShare { get; init; }
    /// <summary>Diagnostics: the rendered frame before the downscale (BGRA, <see cref="MapThumbnailKey.RenderWidth"/> by
    /// <see cref="MapThumbnailKey.RenderHeight"/>), only when the renderer was asked to keep it.</summary>
    public byte[]? Frame { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>The part of the renderer the worker thread needs, so the service's queue, cancellation and caching can be tested
/// without a GPU.</summary>
public interface IMapThumbnailRenderer : IDisposable
{
    /// <summary>Draw one job. Called on ONE thread for the renderer's whole life - the thread that owns its device.</summary>
    MapThumbnailOutcome Render(MapThumbnailJob job);
    /// <summary>Give the device, the shader cache and everything held between jobs back (the worker calls this when idle).</summary>
    void ReleaseDevice();
}

/// <summary>
/// <para>M807: draws a map the way the editor's D3D11 viewport would show it when a game starts, into a small picture for the
/// Content Browser. Nothing here is a second renderer: it is the viewport's own pipeline - <see cref="MapGeoDecoder"/>,
/// <see cref="Dx11SceneBuilder"/>'s Prepare and Commit, <see cref="D3D11MapProps"/> over <see cref="PropMeshBuilder"/>'s props,
/// <see cref="ShaderPreviewRenderer.RenderFrame"/> - driven without a window, a view model or the UI thread.</para>
///
/// <para><b>Threading.</b> <see cref="Render"/> must always be called from the same thread: that thread owns the device (D3D11
/// resource creation is free-threaded, the immediate context is not), the shader cache reader (a WAD stream, not thread-safe)
/// and everything uploaded. The service runs it on one dedicated background thread and calls <see cref="ReleaseDevice"/> from
/// it when idle.</para>
///
/// <para><b>What is drawn.</b> The map in its start state (<see cref="MapStartState"/>): the shipping bin's initial visibility,
/// every event off, the map's own sun, no skybox, no particles, no dynamic lights. Placed props and level props stand where
/// the map puts them, at rest, drawn through Riot's shaders; esports banners are event content and stay off. Textures are
/// reduced to 256 on a side as they decode (<see cref="Dx11SceneBuilder.PrepareLimits.Thumbnail"/>).</para>
/// </summary>
public sealed class MapThumbnailRenderer : IMapThumbnailRenderer
{
    private const float Seconds = 3f;   // the TIME constant: a fixed moment, so the same map always draws the same picture
    private static readonly Vector4 ClearColor = new(0.039f, 0.051f, 0.075f, 1f);   // the editor viewport's own background

    private ShaderPreviewRenderer? _renderer;
    private ShaderCacheReader? _cache;
    private ShaderPermutationIndex? _perms;
    private string? _finalDir;
    private string? _deviceError;
    /// <summary>A job died inside the device (an exception, a frame that would not render): its state is not trusted, so the next
    /// job starts on a new device instead of failing on a lost one until the idle release.</summary>
    private bool _discardDevice;
    private readonly Action<string>? _log;

    /// <param name="log">Where a one-line account of what happened goes; called on the worker thread.</param>
    public MapThumbnailRenderer(Action<string>? log = null) => _log = log;

    /// <summary>What <see cref="Dx11SceneBuilder.Prepare(ShaderCacheReader, ShaderPermutationIndex?, MapGeoAsset, IReadOnlyList{MaterialBinding}, Func{ulong, byte[]?}, string?, string?, bool, Dx11SceneBuilder.PrepareLimits)"/>
    /// may spend on textures. The thumbnail budget; a harness can pass <see cref="Dx11SceneBuilder.PrepareLimits.None"/> to see
    /// whether the budget changes a picture.</summary>
    public Dx11SceneBuilder.PrepareLimits Limits { get; init; } = Dx11SceneBuilder.PrepareLimits.Thumbnail;

    /// <summary>Diagnostics: log every drawn slice's bounds before the camera is framed.</summary>
    public bool DumpSlices { get; init; }

    /// <summary>Diagnostics: return the full-size frame in <see cref="MapThumbnailOutcome.Frame"/>.</summary>
    public bool KeepFrame { get; init; }

    /// <summary>Props are part of the picture. A test turns them off to see what they add; nothing else does.</summary>
    public bool DrawProps { get; init; } = true;

    /// <summary>Diagnostics: asked after the heavy stages, its text goes into the timings (the harness passes process memory).</summary>
    public Func<string>? MemoryProbe { get; init; }
    private string Mem() => MemoryProbe is null ? "" : $" [{MemoryProbe()}]";

    public MapThumbnailOutcome Render(MapThumbnailJob job)
    {
        try { return RenderJob(job) with { BytesRead = job.BytesRead }; }
        // the collection runs here, after RenderJob has returned: inside it every local of the job (the decoded map, the textures,
        // the scene) is still reachable from its own frame, so a collection there would free none of it
        finally { CollectAfterJob(job.BytesRead); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private MapThumbnailOutcome RenderJob(MapThumbnailJob job)
    {
        var total = Stopwatch.StartNew();
        var timings = new List<string>();
        D3D11MapProps? props = null;
        try
        {
            if (_discardDevice) { _discardDevice = false; ReleaseDevice(); }
            if (!EnsureDevice(job, out string? deviceError))
                return new MapThumbnailOutcome { Failure = deviceError, DeviceFailed = _renderer is null, TotalMs = total.Elapsed.TotalMilliseconds };
            var renderer = _renderer!;
            var cache = _cache!;
            Check(job);

            // ---- inputs
            var step = Stopwatch.StartNew();
            byte[]? mapBytes = job.ReadCounted(job.MapGeoHash);
            if (mapBytes is null) return Fail("the mapgeo could not be read", false, total);
            byte[]? materialsBin = job.ReadCounted(job.MaterialsBinHash);
            if (materialsBin is null) return Fail($"{job.MaterialsBinPath} could not be read", false, total);
            byte[]? shippingBin = ReadShippingBin(job);
            timings.Add($"read {step.ElapsedMilliseconds} ms");
            Check(job);

            // ---- decode
            step.Restart();
            MapGeoAsset map;
            MaterialDocument doc;
            try
            {
                map = MapGeoDecoder.Decode(mapBytes, ExtendedChannelRule.From(materialsBin, job.BinName));
                doc = MaterialDocument.Parse(materialsBin, job.BinName, job.WadPath);
            }
            // an exception is not a fact about the map (a read torn by a mount rebuilt under the job decodes to garbage too):
            // not remembered on disk, the next session tries again
            catch (Exception ex) { return Fail($"the map would not decode: {ex.Message}", false, total); }
            if (map.Groups.Count == 0 || map.Indices.Length == 0) return Fail("the mapgeo holds no geometry", true, total);
            timings.Add($"decode {step.ElapsedMilliseconds} ms");
            Check(job);

            // ---- the start state, the sun, the grass tint
            step.Restart();
            var controllerBins = new List<byte[]>();
            foreach (ulong hash in job.ControllerBins)
                if (hash != job.MaterialsBinHash && job.ReadCounted(hash) is { } sibling) controllerBins.Add(sibling);
            controllerBins.Add(materialsBin);   // the map's own bin last: it wins a repeated controller hash
            long t = step.ElapsedMilliseconds;
            var shipping = ShippingFor(shippingBin, job.BinName);
            var stages = MapBoardStages.Load(materialsBin);   // a byte scan for every map but a TFT board
            var board = stages.HasStages ? stages.Stages[0] : null;
            var state = MapStartState.Resolve(map, shipping.Definition, controllerBins, board);
            timings.Add($"visibility {step.ElapsedMilliseconds - t} ms");
            t = step.ElapsedMilliseconds;
            // the lighting the board opens with (M785), then the stage's, laid over it exactly as picking the stage does
            var startSun = MapLighting.EffectiveSun(materialsBin, () => state.Definition.Primary?.InitialMask);
            var authoredSun = board is null ? startSun : stages.SunFor(board, startSun);
            var sun = MapSunRender.RenderForm(authoredSun);
            timings.Add($"sun {step.ElapsedMilliseconds - t} ms");
            t = step.ElapsedMilliseconds;
            string? grassTint = ResolveGrassTint(job, shipping.State, state);
            timings.Add($"grass {step.ElapsedMilliseconds - t} ms");

            // ---- the map's scene: CPU half, then the device
            step.Restart();
            // the texture loop asks, per texture, whether to wait (the editor is loading a map) or stop (the job is cancelled)
            var limits = Limits with { Checkpoint = () => Proceed(job) };
            var prepared = Dx11SceneBuilder.Prepare(cache, _perms, map, doc.Materials, job.ReadCounted, job.MapGeoPath, grassTint,
                pinDynamicLighting: false, limits);
            timings.Add($"prepare {step.ElapsedMilliseconds} ms{Mem()}");
            Check(job);   // a loop that was told to stop returns a scene with textures missing: this throws before anything uses it
            if (prepared.Slices.Count == 0)
            {
                string why = prepared.FailureReasons.Count > 0 ? ": " + string.Join("; ", prepared.FailureReasons.Select(kv => $"{kv.Key} ({Short(kv.Value)})").Take(2)) : "";
                // the reasons include exceptions caught per material: not a fact to write down
                return Fail("no material of the map resolved to a shader" + why, false, total);
            }

            step.Restart();
            var commit = Dx11SceneBuilder.Commit(renderer, prepared, AppInfo.DisplayVersion, job.ClientDepthRules);
            prepared = null!;   // the CPU copies of the textures can go now; the GPU holds them
            timings.Add($"commit {step.ElapsedMilliseconds} ms{Mem()}");
            Check(job);

            // ---- what the start state draws
            int hidden = 0, shown = 0;
            foreach (var m in renderer.Materials)
            {
                int g = m.MapGroupIndex;
                if (g < 0) continue;
                m.Visible = g >= state.GroupVisible.Count || state.GroupVisible[g];
                if (m.Visible) shown++; else hidden++;
            }
            if (shown == 0) return Fail("the map's start state draws nothing (every mesh is hidden)", true, total);

            // ---- the props
            step.Restart();
            int propMeshes = 0, propInstances = 0, propsDrawn = 0;
            props = new D3D11MapProps(renderer, cache) { UploadBudgetMs = double.PositiveInfinity };
            var propSet = DrawProps ? BuildProps(job, materialsBin, state, out propMeshes, out propInstances) : null;
            if (propSet is not null)
            {
                var lightGrid = ReadLightGrid(job, materialsBin);
                var propCache = cache;
                props.Load(propSet,
                    mesh => PropMeshBuilder.PrepareDx11Scene(mesh, propCache, _perms, job.ReadCounted, job.BinName, job.WadPath),
                    world => lightGrid is null ? null
                        : new PropLighting(LightGridFile.ToLightGridColors(lightGrid.SampleAmbient(world)), lightGrid.LightGridScale));
                // all of them now - there is no frame to keep short (the budget is unlimited)
                for (int guard = 0; props.UploadsPending > 0 && guard < 100_000; guard++) { props.PumpUploads(); Check(job); }
                propsDrawn = props.PropInstanceCount;
            }
            timings.Add($"props {step.ElapsedMilliseconds} ms{Mem()}");
            Check(job);

            // ---- the camera, then the frame
            // the camera frames where the DRAWN triangles are: the index ranges of the map slices the start state shows
            var drawn = renderer.Materials.Where(m => m.MapGroupIndex >= 0 && m.Visible && m.IndexCount > 0)
                .Select(m => (m.StartIndex, m.IndexCount)).ToList();
            var centroids = MapThumbnailCamera.SampleCentroids(map.Positions, map.Indices, drawn);
            if (DumpSlices && _log is not null)
                foreach (var m in renderer.Materials.Where(m => m.MapGroupIndex >= 0 && m.Visible && m.Bounds is not null))
                {
                    var b = m.Bounds!.Value;
                    _log($"slice {m.Name}: x {b.Min.X:0}..{b.Max.X:0} y {b.Min.Y:0}..{b.Max.Y:0} z {b.Min.Z:0}..{b.Max.Z:0} idx {m.IndexCount}");
                }
            float aspect = (float)MapThumbnailKey.RenderWidth / MapThumbnailKey.RenderHeight;
            if (!MapThumbnailCamera.TryFrame(centroids, aspect, out var frame))
                return Fail("the drawn map has no extent to frame", true, total);

            step.Restart();
            var settings = new PreviewSettings
            {
                SuppliedView = frame.View, SuppliedProjection = frame.Projection, SuppliedCameraPosition = frame.Eye,
                // the viewport's own state for a map: M240's preset, the editor's defaults for the toggles
                // SortByPipeline off under Game Depth, as the viewport does (submission order stands when the client's depth mask is emulated)
                AlphaBlend = true, DepthTest = true, CullBackFaces = true, SortByPipeline = !job.ClientDepthRules,
                MirrorX = true, TransposeMatrices = true,
                Bloom = true, Shadows = true,
                DebugMode = 1,
                ClearColor = ClearColor, TimeSeconds = Seconds,
                MapSunColor = sun.SunColor, MapSunDirection = sun.SunDirection,
                MapLightMapScale = MapSunRender.LightmapScale(authoredSun),
                MapSun = sun,
                EnvFog = sun.FogEnabled ? MapSunProperties.EnvFogConstants(sun, true) : null,
            };
            byte[]? frameBytes = renderer.RenderFrame(MapThumbnailKey.RenderWidth, MapThumbnailKey.RenderHeight, settings, out string? renderError);
            if (frameBytes is null) { _discardDevice = true; return Fail("the frame could not be rendered: " + renderError, false, total); }
            frameBytes = (byte[])frameBytes.Clone();   // the renderer reuses its readback buffer
            timings.Add($"render {step.ElapsedMilliseconds} ms");

            double share = NonClearShare(frameBytes);
            if (share < 0.005)
                return Fail($"the start state draws nothing visible ({share:P1} of the frame differs from the background)", true, total);

            step.Restart();
            byte[] png = EncodePng(frameBytes);
            timings.Add($"png {step.ElapsedMilliseconds} ms");

            string summary = $"{commit.Materials} material(s), {shown} slice(s) drawn / {hidden} hidden by the start state"
                           + (board is null ? "" : $" ({board.Label}, mask {board.Mask})") + ", "
                           + $"{propInstances} prop instance(s) ({propMeshes} mesh(es), {propsDrawn} placed), {share:P0} of the frame is map; "
                           + $"framed x {frame.BoxMin.X:0}..{frame.BoxMax.X:0} z {frame.BoxMin.Z:0}..{frame.BoxMax.Z:0} floor y {frame.BoxMin.Y:0} "
                           + $"from {frame.Distance:0} (fill {frame.Extent:0.00})";
            return new MapThumbnailOutcome
            {
                Png = png, TotalMs = total.Elapsed.TotalMilliseconds, Timings = string.Join(", ", timings),
                Slices = shown, HiddenSlices = hidden, PropMeshes = propMeshes, PropInstances = propInstances, PropsDrawn = propsDrawn,
                NonClearShare = share, Summary = summary, Frame = KeepFrame ? frameBytes : null,
            };
        }
        catch (OperationCanceledException)
        {
            return new MapThumbnailOutcome { Cancelled = true, Failure = "cancelled", TotalMs = total.Elapsed.TotalMilliseconds };
        }
        catch (Exception ex)
        {
            // an unexpected failure may not recur (a mount closed under the job, a device reset): not remembered
            _discardDevice = true;
            return new MapThumbnailOutcome { Failure = $"{ex.GetType().Name}: {ex.Message}", TotalMs = total.Elapsed.TotalMilliseconds };
        }
        finally
        {
            // whatever the job left on the device goes, success or not: the next job starts from an empty scene, and a
            // Summoner's Rift of vertex and texture memory is not held while the editor sits idle
            try
            {
                props?.Clear();
                if (_renderer is { } r)
                {
                    r.ClearMaterials();
                    r.SetMesh(PreviewGeometry.CreateBuiltIn("Sphere"));
                }
            }
            catch { /* a device that cannot be cleaned is released with the renderer */ }
        }
    }

    /// <summary>What a job read is garbage once it has returned: a Summoner's Rift or Howling Abyss draw reads 150 to 820 MB of
    /// assets and allocates 1 to 3.7 GB turning them into a scene, a TFT board reads about 75 MB. Left to the runtime, the next
    /// maps pile theirs on top before it collects (26 Summoner's Rift maps in a row reached a 5.2 GB working set; a 159-board
    /// TFT folder 2.4 GB). So a job that read anything real is followed by a BACKGROUND collection, which pauses nothing -
    /// measured against a blocking one it peaks the same (3.5 GB for the 26 maps, 1.0 GB for the boards) without a
    /// stop-the-world on a thread the editor shares its heap with. The measure is the job's own reads: process-wide allocation
    /// would count the UI, the viewport and a map load, and the size of the heap would count whatever the editor holds open.</summary>
    private static void CollectAfterJob(long bytesRead)
    {
        if (bytesRead >= CollectAfterReading) GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
    }
    /// <summary>Under this a job did nothing worth a collection - the smallest real draw (an arena) reads 34 MB.</summary>
    private const long CollectAfterReading = 32L * 1024 * 1024;

    // ------------------------------------------------------------------------------------------------ device

    /// <summary>The device, the shader cache and the permutation index - created on the first job and kept (a pipeline cache
    /// hit is most of what makes the second map fast) until <see cref="ReleaseDevice"/>.</summary>
    private bool EnsureDevice(MapThumbnailJob job, out string? error)
    {
        error = null;
        if (_deviceError is not null) { error = _deviceError; return false; }

        if (_renderer is null)
        {
            PinD3D11Library();
            var created = new ShaderPreviewRenderer();
            if (!created.Initialize(out string? initError))
            {
                created.Dispose();
                error = _deviceError = "D3D11 is not available: " + (initError ?? "the device could not be created");
                return false;
            }
            _renderer = created;
        }

        if (_cache is null || !string.Equals(_finalDir, job.FinalDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _cache?.Dispose();
            _perms?.Dispose();
            _cache = ShaderCacheReader.Open(job.FinalDirectory, job.Hashes, out string? cacheError);
            _perms = _cache is null ? null : new ShaderPermutationIndex(job.FinalDirectory, job.WadPath);
            _finalDir = job.FinalDirectory;
            if (_cache is null)
            {
                error = "the shader cache could not be opened: " + (cacheError ?? "unknown");
                return false;
            }
        }
        return true;
    }

    public void ReleaseDevice()
    {
        try { _renderer?.ClearMaterials(); } catch { }
        try { _renderer?.Dispose(); } catch { }
        try { _cache?.Dispose(); } catch { }
        try { _perms?.Dispose(); } catch { }
        _renderer = null; _cache = null; _perms = null; _finalDir = null;
        _shipping.Clear();
        // the managed garbage the last jobs left goes now, in the background. Never a compacting, blocking collection: that is a
        // stop-the-world of the whole editor heap - an open Summoner's Rift's arrays included - from a below-normal thread, and
        // the large-object compaction it asks for would apply to the next full collection of ANYTHING. The cost is that the
        // runtime keeps the freed regions committed instead of handing them back to the OS at once (a working set about a
        // gigabyte higher after an idle release than with a compacting one, measured), which it trims on its own schedule.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
    }

    /// <summary>M807: <c>ShaderPreviewRenderer.Dispose</c> ends by freeing the Silk library handle, which unloads d3d11.dll when
    /// nothing else in the process holds it. When the released device was the process's last one, a thread whose entry point is
    /// inside d3d11.dll has not started running yet and starts in code that has just been unloaded: "Attempt to execute
    /// non-executable address" in &lt;Unloaded_d3d11.dll&gt;, a native crash no catch sees (it hit Map12 runs outside a debugger
    /// and two of two under cdb with the debug heap off; it never showed while another device was alive). One extra load of the
    /// library, never freed, makes that unload a no-op. The device and everything on the GPU are still released; only the
    /// library image stays mapped, and it is the one the viewport maps anyway.</summary>
    private static void PinD3D11Library()
    {
        if (Interlocked.Exchange(ref _d3d11Pinned, 1) != 0) return;
        try { System.Runtime.InteropServices.NativeLibrary.TryLoad("d3d11.dll", out _); }
        catch { /* not Windows, or no such library: there is no device to release either */ }
    }
    private static int _d3d11Pinned;

    public void Dispose() => ReleaseDevice();

    // ------------------------------------------------------------------------------------------------ pieces

    /// <summary>A stage boundary: wait while the editor is busy, then stop if the job was cancelled.</summary>
    private static void Check(MapThumbnailJob job)
    {
        if (!Proceed(job)) throw new OperationCanceledException();
    }

    /// <summary>Wait out <see cref="MapThumbnailJob.ShouldPause"/> (a map loading, a scene building - a job that began before the
    /// user opened a map must not keep decoding gigabytes beside it), asking about cancellation while it does. False once the
    /// job is cancelled; true when it may go on.</summary>
    private static bool Proceed(MapThumbnailJob job)
    {
        while (true)
        {
            if (job.IsCancelled?.Invoke() == true) return false;
            if (job.ShouldPause?.Invoke() != true) return true;
            Thread.Sleep(50);
        }
    }

    private MapThumbnailOutcome Fail(string reason, bool permanent, Stopwatch total) =>
        new() { Failure = reason, Permanent = permanent, TotalMs = total.Elapsed.TotalMilliseconds };

    private static string Short(string s) => s.Length <= 80 ? s : s[..77] + "...";

    /// <summary>The shipping <c>mapNN.bin</c> of a mapgeo, <c>data/maps/shipping/map22/map22.bin</c>.</summary>
    public static string? ShippingBinPathFor(string mapGeoPath)
    {
        var match = System.Text.RegularExpressions.Regex.Match(mapGeoPath, @"/mapgeometry/map(?<id>\d+)/",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? $"data/maps/shipping/map{match.Groups["id"].Value}/map{match.Groups["id"].Value}.bin" : null;
    }

    private static byte[]? ReadShippingBin(MapThumbnailJob job) =>
        ShippingBinPathFor(job.MapGeoPath) is { } path ? job.ReadCounted(HashAlgorithms.WadPath(path)) : null;

    /// <summary>What a map's shipping bin says that a thumbnail reads: its visibility axes and its map state (grass tint).</summary>
    private sealed record ShippingData(MapVisibilityDefinition Definition, MapStateData State);

    /// <summary>The shipping bin is parsed twice for these (about 1.4 s for Map22's) and is the same for every skin of a map,
    /// so the result is kept - by the bin's content, not its path, since a project may carry a bin of its own - until the
    /// device is released.</summary>
    private ShippingData ShippingFor(byte[]? shippingBin, Func<uint, string?> binName)
    {
        if (shippingBin is null) return new ShippingData(MapVisibilityDefinition.Empty, MapStateData.Empty);
        ulong id = System.IO.Hashing.XxHash64.HashToUInt64(shippingBin);
        if (_shipping.TryGetValue(id, out var cached)) return cached;
        var data = new ShippingData(MapVisibility.Parse(shippingBin, binName), MapStateData.Parse(shippingBin, binName));
        if (_shipping.Count >= 16) _shipping.Clear();
        return _shipping[id] = data;
    }

    private readonly Dictionary<ulong, ShippingData> _shipping = new();

    /// <summary>The grass tint the start state selects (MapStateData, as <c>FindGrassTintTexturePath</c> resolves it): the
    /// authored path when the game holds it, else none. The editor's last-resort scan by file name is not repeated.</summary>
    private static string? ResolveGrassTint(MapThumbnailJob job, MapStateData data, MapStartState state)
    {
        try
        {
            var skin = data.SkinForMapGeo(job.MapGeoPath);
            int primaryBit = state.Definition.Primary is { } p ? state.Selections.GetValueOrDefault(p.DefinitionFieldHash) : 0;
            var choice = data.ResolveGrassTintForMask(skin, primaryBit);
            if (string.IsNullOrWhiteSpace(choice.ActivePath)) return null;
            string path = choice.ActivePath.Replace('\\', '/').ToLowerInvariant();
            return job.Has(BinTexturePath.HashOfReference(path)) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>The placed props and level props the start state shows, decoded by <see cref="PropMeshBuilder"/> (the
    /// viewport's own prop decode, <see cref="PropMeshDetail.Dx11Only"/>) and placed as the viewport places them. One mesh per
    /// skin, shared by its placements. Null when there is none.</summary>
    private PropRenderSet? BuildProps(MapThumbnailJob job, byte[] materialsBin, MapStartState state,
        out int meshCount, out int instanceCount)
    {
        meshCount = instanceCount = 0;
        var placed = MapPlaceableExtractor.Extract(materialsBin, job.WadPath).Props.Where(state.PropShown).ToList();
        var level = MapPlaceableExtractor.ExtractLevelProps(materialsBin).Where(state.LevelPropShown).ToList();
        if (placed.Count == 0 && level.Count == 0) return null;

        var builder = new PropMeshBuilder(new PropMeshSources(
            ReadByPath: path => string.IsNullOrEmpty(path) ? null : job.ReadCounted(BinTexturePath.HashOfReference(path)),
            ResolveBinName: job.BinName,
            ResolveWadPath: job.WadPath,
            LoadTexture: _ => null,
            FindClip: (_, _) => null,
            FindIdleClip: _ => null));
        var meshes = new Dictionary<string, PropMesh?>(StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);
        var instances = new List<PropInstanceData>(placed.Count + level.Count);

        void Place(string skin, Matrix4x4 transform)
        {
            if (string.IsNullOrEmpty(skin)) return;
            if (!meshes.TryGetValue(skin, out var mesh))
            {
                Check(job);
                meshes[skin] = mesh = builder.TryBuild(skin, textures, null, null, PropMeshDetail.Dx11Only);
            }
            if (mesh is not null) instances.Add(PropInstanceData.Place(mesh, transform));   // M676: skinScale under the placement
        }
        foreach (var p in placed) Place(p.Skin, p.Transform);
        foreach (var p in level) Place(p.Skin, p.Transform);

        meshCount = meshes.Values.Count(m => m is not null);
        instanceCount = instances.Count;
        return instances.Count > 0 ? new PropRenderSet(instances) : null;
    }

    /// <summary>The map's lightgrid, which lights its props (M680): the file <c>MapBakeProperties</c> names, when it is there.</summary>
    private static LightGridFile? ReadLightGrid(MapThumbnailJob job, byte[] materialsBin)
    {
        try
        {
            if (MapBakeProperties.Read(materialsBin) is not { File.Length: > 0 } bake) return null;
            var bytes = job.ReadCounted(BinTexturePath.HashOfReference(bake.File));
            return bytes is not null && LightGridFile.LooksLikeLightGrid(bytes) ? LightGridFile.Read(bytes) : null;
        }
        catch { return null; }
    }

    /// <summary>The share of a BGRA frame whose colour is not the clear colour (by more than a few levels a channel).</summary>
    public static double NonClearShare(byte[] bgra)
    {
        int pixels = bgra.Length / 4;
        if (pixels == 0) return 0;
        byte b = (byte)(ClearColor.Z * 255f + 0.5f), g = (byte)(ClearColor.Y * 255f + 0.5f), r = (byte)(ClearColor.X * 255f + 0.5f);
        int different = 0;
        for (int i = 0; i < pixels; i++)
        {
            int o = i * 4;
            if (Math.Abs(bgra[o] - b) > 6 || Math.Abs(bgra[o + 1] - g) > 6 || Math.Abs(bgra[o + 2] - r) > 6) different++;
        }
        return (double)different / pixels;
    }

    /// <summary>The rendered BGRA frame, opaque, Lanczos-downscaled to the stored size (as the cinematic capture downscales a
    /// supersampled frame) and written as an RGB PNG.</summary>
    public static byte[] EncodePng(byte[] bgra)
    {
        int w = MapThumbnailKey.RenderWidth, h = MapThumbnailKey.RenderHeight;
        if (bgra.Length < w * h * 4) throw new ArgumentException("the frame is smaller than the render size", nameof(bgra));
        // the readback's alpha is whatever the shaders last wrote: a thumbnail is opaque
        var opaque = new byte[w * h * 4];
        Buffer.BlockCopy(bgra, 0, opaque, 0, opaque.Length);
        for (int i = 3; i < opaque.Length; i += 4) opaque[i] = 255;

        using var image = Image.LoadPixelData<Bgra32>(opaque, w, h);
        image.Mutate(x => x.Resize(MapThumbnailKey.ThumbWidth, MapThumbnailKey.ThumbHeight, KnownResamplers.Lanczos3));
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder { ColorType = PngColorType.Rgb });
        return ms.ToArray();
    }
}
