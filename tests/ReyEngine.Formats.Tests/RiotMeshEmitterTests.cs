using System.Numerics;
using ReyEngine.App.Services;
using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Meta;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Vfx;
using ReyEngine.Rendering.D3D11;
using ReyEngine.Rendering.Vfx;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M640: mesh-primitive emitters draw through Riot's own <c>particlesystem/mesh_vs</c> + <c>mesh_ps</c> on
/// D3D11, and both renderers apply the authored Euler birth rotation and per-axis birth scale that were
/// dropped before. Measured on ten champions: 551 mesh emitters, 398 with a non-zero birth rotation and
/// 271 with a non-uniform birth scale - the two things no renderer honoured.
/// </summary>
public sealed class RiotMeshEmitterTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";
    private const string Champions = Final + @"\Champions";
    private static bool Installed => Directory.Exists(Final);
    private const int Stride = 19;

    private static string? Source(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static VfxSystemDefinition One(bool mesh, Vector3 birthRotationDegrees, Vector3 birthScale) => new(
        PathHash: 1, Name: "m", ParticlePath: "",
        Emitters: new[]
        {
            new VfxEmitterDefinition(
                Name: "m", Rate: VfxCurveF.Const(1f), ParticleLifetime: VfxCurveF.Const(5f), EmitterLifetime: null,
                ParticleLinger: 0f, TimeBeforeFirstEmission: 0f, IsSingleParticle: true, Disabled: false, BlendMode: 1,
                BirthScale: VfxCurve3.Const(birthScale), ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One), ColorOverLife: null,
                BirthVelocity: null, Acceleration: null, BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: "ASSETS/Test/p.dds", TexDiv: Vector2.One, NumFrames: 1, RandomStartFrame: false,
                IsMeshPrimitive: mesh, MeshPath: mesh ? "ASSETS/Test/m.scb" : null,
                BirthRotation: VfxCurve3.Const(birthRotationDegrees)),
        });

    private static float[] FirstInstance(VfxSystemDefinition system)
    {
        var sim = new VfxParticleSimulator(seed: 1);
        sim.SetSystem(system, Matrix4x4.Identity);
        for (int i = 0; i < 60; i++)
        {
            sim.Update(1f / 30f);
            var e = sim.Emitters.Single();
            if (e.InstanceCount > 0) return e.Instances.Take(Stride).ToArray();
        }
        throw new Xunit.Sdk.XunitException("no particle was born in two seconds");
    }

    // ===================================================== the instance packing

    [Fact]
    public void AMeshParticleCarriesItsEulerBirthRotationAndPerAxisScale()
    {
        var i = FirstInstance(One(mesh: true, new Vector3(30f, 0f, 35f), new Vector3(2f, 1f, 1.5f)));

        // Slots 15-17: the authored birth rotation in radians. X used to be the spin, which starts at 0
        // for a mesh, so Aatrox's P_Debuff symbols (30, 0, 0) lost their tilt entirely.
        Assert.Equal(30f * MathF.PI / 180f, i[15], 4);
        Assert.Equal(0f, i[16], 4);
        Assert.Equal(35f * MathF.PI / 180f, i[17], 4);
        Assert.Equal(0f, i[9], 4);                          // the over-life spin starts at rest

        // Slots 3, 4 and 10: the per-axis scale. Slot 10 is the flipbook frame on a quad; a mesh tiles
        // its texture instead of flipping it, so the slot is free to carry Z.
        Assert.Equal(2f, i[3], 4);
        Assert.Equal(1f, i[4], 4);
        Assert.Equal(1.5f, i[10], 4);
    }

    [Fact]
    public void AQuadIsPackedExactlyAsBefore()
    {
        // The same emitter as a billboard: slot 15 is the spin (= birth X), slot 10 is the frame.
        var i = FirstInstance(One(mesh: false, new Vector3(30f, 0f, 35f), new Vector3(2f, 1f, 1.5f)));
        Assert.Equal(30f * MathF.PI / 180f, i[9], 4);
        Assert.Equal(i[9], i[15], 4);
        Assert.Equal(0f, i[10], 4);
    }

    [Fact]
    public void AZeroZScaleFallsBackToXLikeY()
    {
        // The M239 junk guard, extended to Z: a zero component is "not authored", not "flat".
        var i = FirstInstance(One(mesh: true, Vector3.Zero, new Vector3(3f, 0f, 0f)));
        Assert.Equal(3f, i[3], 4);
        Assert.Equal(3f, i[4], 4);
        Assert.Equal(3f, i[10], 4);
    }

    // ===================================================== what Riot's mesh shaders declare

    [Fact]
    public void RiotsMeshShadersDeclareExactlyWhatTheDriverSends()
    {
        // The constants the driver fills by name, read off the shipped bytecode rather than assumed:
        // mWorld per draw, kColorFactor + vParticleUVTransform (+Mult under MULT_PASS, vFresnel under
        // REFLECTIVE) in $Globals, and a POSITION/NORMAL/TEXCOORD vertex.
        if (!Installed) return;
        var database = new HashSyncService().LoadLocal(_ => { });
        using var cache = ShaderCacheReader.Open(Final, new WadPathResolver(database), out _);
        if (cache is null) return;

        var tocs = VfxD3D11EmitterPipeline.ReadMeshTocs(cache, out var error);
        Assert.True(tocs is not null, error);
        Assert.Equal(VfxD3D11EmitterPipeline.MeshVsName, tocs!.VsName);
        Assert.Equal(VfxD3D11EmitterPipeline.MeshPsName, tocs.PsName);

        string toc = ShaderCacheReader.TocPathFor(VfxD3D11EmitterPipeline.MeshVsName, DxbcStage.Vertex);
        var described = ShaderCacheReader.DescribePermutations(tocs.Vs, out _);
        var basePerm = described.First(p => p.Defines is null || p.Defines.Count == 0);
        var mult = described.First(p => p.Defines is { Count: 1 } d && d[0].StartsWith("MULT_PASS", StringComparison.Ordinal));
        var refl = described.First(p => p.Defines is { Count: 1 } d && d[0].StartsWith("REFLECTIVE", StringComparison.Ordinal));

        foreach (var (perm, extra) in new[] { (basePerm, (string?)null), (mult, "vParticleUVTransformMult"), (refl, "vFresnel") })
        {
            var blob = cache.LoadShader(toc, perm.BlobIndex, out var e);
            Assert.True(blob is not null, e);
            var globals = blob!.ConstantBuffers.Single(c => c.Name == "$Globals");
            foreach (string name in new[] { "kColorFactor", "vParticleUVTransform" }.Concat(extra is null ? Array.Empty<string>() : new[] { extra }))
            {
                var v = globals.Variables.FirstOrDefault(x => x.Name == name);
                Assert.True(v is not null && v.IsUsed, $"blob {perm.BlobIndex}: {name} not declared or unused");
            }
            var perDraw = blob.ConstantBuffers.Single(c => c.Name == "CharacterPerDrawVertexCB");
            Assert.True(perDraw.Variables.Single(x => x.Name == "mWorld").IsUsed);
            Assert.Equal(new[] { "POSITION0", "NORMAL0", "TEXCOORD0" }, blob.Inputs.Select(x => x.FullSemantic).ToArray());
        }
    }

    // ===================================================== the wiring

    [Fact]
    public void BothRenderersApplyTheSameChainAndTheDriverFeedsTheRiotPair()
    {
        var driver = Source("src", "ReyEngine.App", "Services", "D3D11MapParticles.cs");
        var pipeline = Source("src", "ReyEngine.App", "Services", "VfxD3D11EmitterPipeline.cs");
        var d3d = Source("src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs");
        var gl = Source("src", "ReyEngine.Rendering", "Vfx", "VfxParticleRenderer.cs");
        if (driver is null || pipeline is null || d3d is null || gl is null) return;

        // The driver reads the mesh pair once per rebuild, builds static meshes on it, and sends both UV
        // affines. Animated meshes stay on the re-skinning path.
        Assert.Contains("VfxD3D11EmitterPipeline.ReadMeshTocs(_cache, out meshTocError)", driver);
        Assert.Contains("_meshTocs is { } meshTocs && mesh.Animation is null", driver);
        Assert.Contains("riotMat.RiotMeshGeometryId = riotId;", driver);
        Assert.Contains("mat.Params[\"vParticleUVTransform\"]", driver);
        Assert.Contains("mat.Params[\"vParticleUVTransformMult\"]", driver);
        Assert.Contains("mat.Params[\"vFresnel\"]", driver);
        Assert.Contains("riotMat.CullBackFaces = !def.DisableBackfaceCull;", driver);

        // REFLECTIVE is asked only of the pair that has the axis.
        Assert.Contains("if (tocs.VsName == MeshVsName && e.Reflection is { HasFresnel: true })", pipeline);

        // D3D11: scale, Euler X-Y-Z, spin about Y, placement. GL: the same chain in the vertex shader.
        Assert.Contains("* Matrix4x4.CreateRotationX(euler.X)", d3d);
        Assert.Contains("* Matrix4x4.CreateRotationY(inst[o + 9])", d3d);
        Assert.Contains("_riotMeshGeoms.All(g => g is null)", d3d);          // a mesh-only frame is a frame
        Assert.Contains("uniform vec3 uMeshEuler;", gl);
        Assert.Contains("rotateEuler(aPos * uScale, uMeshEuler)", gl);
        Assert.Contains("_gl.Uniform3(_muMeshEuler, es.Instances[o + 15], es.Instances[o + 16], es.Instances[o + 17]);", gl);
        Assert.Contains("_gl.Uniform3(_muScale, sx, sy, sz);", gl);
    }

    // ===================================================== the picture

    /// <summary>Aatrox's E dash dome (two mesh emitters, both with a birth rotation, palette and erosion),
    /// drawn on a real device through both paths. The Riot path must build and draw both meshes, and it
    /// must NOT produce the same picture as the approximation it replaces - the whole point is that the
    /// stages and the rotation now apply.</summary>
    [Fact]
    public void AatroxsEDashDomeDrawsOnRiotsMeshShadersAndDiffersFromTheApproximation()
    {
        if (!Installed || !File.Exists(Path.Combine(Champions, "Aatrox.wad.client"))) return;
        var database = new HashSyncService().LoadLocal(_ => { });
        var resolver = new WadPathResolver(database);
        using var cache = ShaderCacheReader.Open(Final, resolver, out _);
        if (cache is null) return;
        using var archive = WadArchive.Open(Path.Combine(Champions, "Aatrox.wad.client"), resolver);

        var systems = new Dictionary<uint, VfxSystemDefinition>();
        var visited = new HashSet<ulong>();
        var queue = new Queue<ulong>();
        ulong start = HashAlgorithms.WadPath("data/characters/aatrox/skins/skin0.bin");
        visited.Add(start); queue.Enqueue(start);
        for (int guard = 0; queue.Count > 0 && guard < 64; guard++)
        {
            byte[] bytes;
            try { bytes = archive.Extract(queue.Dequeue()); } catch { continue; }
            foreach (var (k, v) in VfxSystemResolver.ExtractAll(bytes)) systems.TryAdd(k, v);
            foreach (var dep in VfxSystemResolver.ExtractDependencies(bytes))
            {
                ulong dh = HashAlgorithms.WadPath(dep);
                if (visited.Add(dh) && archive.TryGetEntry(dh, out _)) queue.Enqueue(dh);
            }
        }
        var system = systems.Values.FirstOrDefault(s => s.Name == "Aatrox_Base_E_Dash2");
        if (system is null) return;
        Assert.Equal(2, system.Emitters.Count(e => e.IsMeshPrimitive && !e.Disabled));

        TextureImage? Load(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            ulong h = BinTexturePath.HashOfReference(path);
            if (!archive.TryGetEntry(h, out _)) return null;
            try { return TextureDecoder.Decode(archive.Extract(h)); } catch { return null; }
        }
        StaticMeshData? LoadMesh(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            ulong h = HashAlgorithms.WadPath(path);
            if (!archive.TryGetEntry(h, out _)) return null;
            try { return StaticObjectDecoder.Decode(archive.Extract(h), path); } catch { return null; }
        }
        var texs = system.Emitters.Select(e => Load(e.TexturePath)).ToList();
        var meshes = system.Emitters.Select(e => e.IsMeshPrimitive ? LoadMesh(e.MeshPath) : null).ToList();
        Assert.Equal(2, meshes.Count(m => m is not null));
        var item = new VfxPlaybackItem(system, VfxCastFrame.Toward(Vector3.Zero, new Vector3(350f, 0f, 0f), Vector3.Zero), texs, meshes,
            EmitterMultTextures: system.Emitters.Select(e => Load(e.TextureMultPath)).ToList(),
            EmitterErosionTextures: system.Emitters.Select(e => Load(e.AlphaErosion?.MapPath)).ToList(),
            EmitterPaletteTextures: system.Emitters.Select(e => Load(e.Palette?.TexturePath)).ToList());

        var eye = new Vector3(0f, 420f, 900f);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0f, 60f, 0f), Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, 5f, 20000f);
        var mirroredView = Matrix4x4.CreateScale(-1f, 1f, 1f) * view;
        const int Size = 320;

        byte[]? Render(bool riot, out D3D11MapParticles driver, out ShaderPreviewRenderer renderer)
        {
            renderer = new ShaderPreviewRenderer();
            driver = null!;
            if (!renderer.Initialize(out _)) return null;
            driver = new D3D11MapParticles(renderer, cache) { UseRiotMeshShaders = riot };
            driver.SetPlayback(new VfxPlayback(new[] { item }));
            for (int i = 0; i < 9; i++) driver.Tick(1f / 60f, mirroredView, mirroredView * proj, eye, 1000f);   // 0.15 s: the dome is up
            var s = new PreviewSettings
            {
                SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
                AlphaBlend = true, DepthTest = true, MirrorX = true, TransposeMatrices = true,
                CullBackFaces = false, SortByPipeline = false,
                ClearColor = new Vector4(0.039f, 0.051f, 0.075f, 1f), TimeSeconds = 0.15f,
            };
            var frame = renderer.RenderFrame(Size, Size, s, out _);
            return frame is null ? null : (byte[])frame.Clone();
        }

        var approx = Render(false, out var approxDriver, out var r1);
        using (r1) { if (approx is null) return; approxDriver.StopAll(); }   // no device: nothing to measure
        var riotFrame = Render(true, out var riotDriver, out var r2);
        using (r2)
        {
            Assert.NotNull(riotFrame);
            Assert.Equal(2, riotDriver.RiotMeshEmitters);
            Assert.Equal(2, riotDriver.MeshEmittersDrawn);
            Assert.Equal(0, riotDriver.SkippedMeshEmitters);
            riotDriver.StopAll();
        }
        Assert.Equal(0, approxDriver.RiotMeshEmitters);
        Assert.Equal(2, approxDriver.MeshEmittersDrawn);

        long covered = 0, changed = 0;
        for (int i = 0; i + 3 < riotFrame!.Length; i += 4)
        {
            if (Math.Abs(riotFrame[i] - 19) > 6 || Math.Abs(riotFrame[i + 1] - 13) > 6 || Math.Abs(riotFrame[i + 2] - 10) > 6) covered++;
            int d = Math.Max(Math.Abs(riotFrame[i] - approx[i]), Math.Max(Math.Abs(riotFrame[i + 1] - approx[i + 1]), Math.Abs(riotFrame[i + 2] - approx[i + 2])));
            if (d > 2) changed++;
        }
        Assert.True(covered > 1000, $"the Riot path covered {covered} px");
        Assert.True(changed > 1000, $"the Riot path moved only {changed} px against the approximation");
    }
}
