using System.Numerics;
using System.Text;
using ReyEngine.App.Services;
using ReyEngine.Core.Hashing;
using ReyEngine.Core.Wad;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Shaders;
using ReyEngine.Rendering.D3D11;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M646: Shaders/SkinnedMesh/Onsen (Locke), and the three things it exposed.
///
/// <para>The .skn decoder never read PrimaryColor, so every champion shader got COLOR0 = white; and when
/// it did read it, it handed the bytes out in the order LeagueToolkit LABELS them rather than the order
/// the file holds them - Onsen smoothsteps COLOR0.z against VCDissolve_Value, and z came out as the byte
/// that is 0 everywhere instead of the painted height gradient, which discarded the body whole.</para>
///
/// <para>The materials carry a <c>dynamicMaterial</c>: the authored VCDissolve_Value (1.23, which dissolves
/// everything below a quarter of his height) is driven to -0.8 while alive and to 3 over eight seconds
/// once dead. A preview drawing the authored value draws him dead.</para>
///
/// <para>And the D3D11 character path posed its bones in VIEW space with mProj = projection alone while
/// feeding vCamera as a WORLD position - geometrically identical, but every constant that takes the
/// skinned position as world (the view vector behind every fresnel, the fog-of-war lookup, the depth
/// push) was wrong. The evidence for the world-space reading is pinned off the bytecode below.</para>
/// </summary>
public sealed class OnsenCharacterTests
{
    private const string Final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

    private static readonly Lazy<HashDatabase?> Database = new(() =>
    {
        try { return new HashSyncService().LoadLocal(_ => { }); }
        catch { return null; }
    });

    private sealed record Fixture(byte[] Skn, byte[] SkinBin, ShaderCacheReader Cache, WadArchive Archive, HashDatabase Database) : IDisposable
    {
        public string? BinName(uint h) => Database.TryGetBinName(h, out var n) ? n : null;
        public string? WadPath(ulong h) => Database.TryGetPath(h, out var p) ? p : null;
        public byte[]? Read(ulong h) => Archive.TryGetEntry(h, out _) ? Archive.Extract(h) : null;
        public void Dispose() { Cache.Dispose(); Archive.Dispose(); }
    }

    private static Fixture? Locke()
    {
        string wad = Path.Combine(Final, "Champions", "Locke.wad.client");
        if (!File.Exists(wad) || Database.Value is not { } database) return null;
        var resolver = new WadPathResolver(database);
        var archive = WadArchive.Open(wad, resolver);
        var cache = ShaderCacheReader.Open(Final, resolver, out _);
        ulong sknHash = HashAlgorithms.WadPath("assets/characters/locke/skins/base/locke_base.skn");
        ulong binHash = HashAlgorithms.WadPath("data/characters/locke/skins/skin0.bin");
        byte[]? skn = archive.TryGetEntry(sknHash, out _) ? archive.Extract(sknHash) : null;
        byte[]? bin = archive.TryGetEntry(binHash, out _) ? archive.Extract(binHash) : null;
        if (cache is null || skn is null || bin is null) { archive.Dispose(); cache?.Dispose(); return null; }
        return new Fixture(skn, bin, cache, archive, database);
    }

    private static unsafe string Disassemble(byte[] bytecode)
    {
        var compiler = D3DCompiler.GetApi();
        ID3D10Blob* text = null;
        int hr;
        fixed (byte* bytes = bytecode)
            hr = compiler.Disassemble(bytes, (nuint)bytecode.Length, 0, (byte*)null, &text);
        if (hr < 0 || text is null) return "";
        string listing = Encoding.ASCII.GetString(new ReadOnlySpan<byte>(text->GetBufferPointer(), (int)text->GetBufferSize())).TrimEnd('\0');
        text->Release();
        return listing;
    }

    // ===================================================== the vertex colour

    /// <summary>The .skn v4 layout, read with nothing between the test and the bytes: where the vertices
    /// start, their stride, and the submesh table. The colour is the last four bytes of a 56-byte vertex.</summary>
    private static (int VertexBase, int Stride, List<(string Name, int StartVertex, int VertexCount)> Submeshes) RawSkn(byte[] d)
    {
        Assert.Equal(0x00112233u, BitConverter.ToUInt32(d, 0));
        Assert.Equal(4, BitConverter.ToUInt16(d, 4));
        int o = 8;
        int subCount = BitConverter.ToInt32(d, o); o += 4;
        var subs = new List<(string, int, int)>();
        for (int i = 0; i < subCount; i++)
        {
            string name = Encoding.ASCII.GetString(d, o, 64).TrimEnd('\0'); o += 64;
            subs.Add((name, BitConverter.ToInt32(d, o), BitConverter.ToInt32(d, o + 4))); o += 16;
        }
        o += 4;                                            // flags
        int indexCount = BitConverter.ToInt32(d, o); o += 4;
        o += 4;                                            // vertex count
        int stride = BitConverter.ToInt32(d, o); o += 4;
        o += 4;                                            // vertex type
        o += 24 + 16;                                      // bounding box, bounding sphere
        o += 2 * indexCount;
        return (o, stride, subs);
    }

    [Fact]
    public void TheDecoderHandsTheColourBytesOutInFileOrder()
    {
        // COLOR0.z in the shader is byte 2 of the element. Locke paints a height gradient there - 1 at
        // the feet, 238 at the crown - and nothing in bytes 0 and 1; under the labelled order the shader
        // would read z = byte 0 = 0 and dissolve the whole body.
        if (Locke() is not { } f) return;
        using (f)
        {
            var mesh = SkinnedMeshDecoder.Decode(f.Skn);
            Assert.NotNull(mesh.Colors);
            Assert.Equal(mesh.VertexCount * 4, mesh.Colors!.Length);

            var (vertexBase, stride, subs) = RawSkn(f.Skn);
            Assert.Equal(56, stride);
            var body = subs.Single(s => s.Name == "Body");
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sum0 = 0, sum1 = 0;
            for (int v = body.StartVertex; v < body.StartVertex + body.VertexCount; v++)
            {
                int at = vertexBase + v * stride + 52;
                for (int k = 0; k < 4; k++)
                    Assert.Equal(f.Skn[at + k] / 255f, mesh.Colors[v * 4 + k]);
                float y = BitConverter.ToSingle(f.Skn, vertexBase + v * stride + 4);
                float z = mesh.Colors[v * 4 + 2];
                sx += z; sy += y; sxx += z * z; syy += y * y; sxy += z * y;
                sum0 += mesh.Colors[v * 4]; sum1 += mesh.Colors[v * 4 + 1];
            }
            int n = body.VertexCount;
            double cov = sxy / n - (sx / n) * (sy / n);
            double corr = cov / Math.Sqrt((sxx / n - (sx / n) * (sx / n)) * (syy / n - (sy / n) * (sy / n)));
            Assert.True(corr > 0.95, $"COLOR0.z is not the painted height gradient (correlation with bind Y {corr:0.00})");
            Assert.True(sum0 / n < 0.05 && sum1 / n < 0.05, $"bytes 0 and 1 carry data ({sum0 / n:0.00}, {sum1 / n:0.00}) - the file order was read wrong");
            Assert.True(sx / n > 0.4, $"the gradient is not in z (mean {sx / n:0.00})");
        }
    }

    // ===================================================== the runtime drivers

    [Fact]
    public void OmittedMaterialValuesAreZeroAndRemainSparseUntilEdited()
    {
        using var f = Locke();
        if (f is null) return; // Same local-data requirement as the other real-skin tests here.
        var doc = MaterialDocument.Parse(f.SkinBin, f.BinName, f.WadPath);
        var body = doc.Materials.Single(m => m.Name.EndsWith("Locke_Body_inst", StringComparison.OrdinalIgnoreCase));
        var distortion = Assert.Single(body.Parameters, p => p.Name == "DiffuseTexDistortIntensity");
        Assert.True(distortion.TryGetVector4(out var zero));
        Assert.Equal(Vector4.Zero, zero);
        Assert.False(doc.IsDirty);
        var original = doc.Serialize();

        distortion.Apply("0.25, 0, 0, 0");
        Assert.True(doc.IsDirty);
        var edited = MaterialDocument.Parse(doc.Serialize(), f.BinName, f.WadPath);
        var saved = edited.Materials.Single(m => m.Name == body.Name).Parameters.Single(p => p.Name == distortion.Name);
        Assert.True(saved.TryGetVector4(out var value));
        Assert.Equal(0.25f, value.X);
        distortion.Revert();
        Assert.False(doc.IsDirty);
        Assert.Equal(original, doc.Serialize());

        var scene = Dx11CharacterScene.Prepare(f.Skn, f.SkinBin, f.Cache, new ShaderPermutationIndex(Final),
            f.Read, f.BinName, f.WadPath, fallbackShader: Dx11CharacterScene.DefaultCharacterShader);
        Assert.NotNull(scene);
        Assert.Equal(0f, scene.Slices.First(s => s.Submesh == "Body").Parameters
            .Single(p => p.Name == "DiffuseTexDistortIntensity").Value[0]);
    }

    [Fact]
    public void LockesMaterialsDriveTheirDissolveAndTransitionAtRuntime()
    {
        if (Locke() is not { } f) return;
        using (f)
        {
            var doc = MaterialDocument.Parse(f.SkinBin, f.BinName, f.WadPath);
            var body = doc.Materials.First(m => m.Name.EndsWith("Locke_Body_inst", StringComparison.OrdinalIgnoreCase));
            var drivers = body.DynamicParameters.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

            // The authored 1.23 is the editor's value. Alive he is at -0.8 (nothing dissolves), dead at 3.
            Assert.Equal(1.23f, body.Parameters.First(p => p.Name == "VCDissolve_Value").TryGetVector4(out var authored) ? authored.X : float.NaN, 3);
            var dissolve = drivers["VCDissolve_Value"];
            Assert.True(dissolve.Enabled);
            Assert.Equal(-0.8f, dissolve.RestValue!.Value.X, 3);
            Assert.Equal(3f, dissolve.ActiveValue!.Value.X, 3);
            Assert.Contains("IsDead", dissolve.Driver, StringComparison.Ordinal);
            Assert.Contains("mOffValue", dissolve.RestReason, StringComparison.Ordinal);

            // The wet-state transition rides his W buff: -0.2 without it, 1.6 with it.
            var transition = drivers["Transition_Value"];
            Assert.Equal(-0.2f, transition.RestValue!.Value.X, 3);
            Assert.Equal(1.6f, transition.ActiveValue!.Value.X, 3);
            Assert.Contains("HasBuff", transition.Driver, StringComparison.Ordinal);
            Assert.Contains("LockeW", transition.Driver, StringComparison.Ordinal);

            // Written disabled: dead data, and reported as such rather than applied.
            Assert.False(drivers["Dissolve_Bias"].Enabled);
            // M647: a lerp whose branch Locke never writes reads its SCHEMA default (mOffValue 0), not
            // "unknown" - see MaterialDriverStateTests, which pins those defaults against meta.db.json.
            Assert.Equal(0f, drivers["Dissolve_LerpOverride"].RestValue!.Value.X, 3);
            Assert.Contains("mOffValue", drivers["Dissolve_LerpOverride"].RestReason, StringComparison.Ordinal);
            // And the colour graph over that lerp samples at 0, which is its first key.
            Assert.Contains("ColorGraph", drivers["Dissolve_Color"].Driver, StringComparison.Ordinal);
            Assert.NotNull(drivers["Dissolve_Color"].RestValue);

            // All five Onsen materials carry the same drivers.
            foreach (var m in doc.Materials.Where(m => m.Name.EndsWith("_inst", StringComparison.OrdinalIgnoreCase)))
                Assert.Contains(m.DynamicParameters, d => d.Name == "Transition_Value" && d.RestValue?.X == -0.2f);

            // And the scene draws the rest values, not the authored ones.
            var scene = Dx11CharacterScene.Prepare(f.Skn, f.SkinBin, f.Cache, new ShaderPermutationIndex(Final),
                f.Read, f.BinName, f.WadPath, fallbackShader: Dx11CharacterScene.DefaultCharacterShader);
            Assert.NotNull(scene);
            var bodySlice = scene!.Slices.First(sl => sl.Submesh == "Body");
            Assert.Equal(-0.8f, bodySlice.Parameters.Single(p => p.Name == "VCDissolve_Value").Value[0], 3);
            Assert.Equal(-0.2f, bodySlice.Parameters.Single(p => p.Name == "Transition_Value").Value[0], 3);
            Assert.Contains("VCDissolve_Value = -0.8 (IsDead is off -> mOffValue)", scene.Report, StringComparison.Ordinal);
        }
    }

    // ===================================================== the space the shader works in

    [Fact]
    public void OnsensVertexShaderSubtractsTheCameraFromTheSkinnedPosition()
    {
        // The evidence for world-space bones: the VS forms (skinned position - vCamera) and a fog-of-war
        // UV from the skinned position's xz. Both only make sense if the skinned position is world-space,
        // because vCamera is (M231, quad_vs) and the fog-of-war map is a world-space map.
        if (Locke() is not { } f) return;
        using (f)
        {
            string full = "assets/shaders/generated/shaders/skinnedmesh/onsen";
            string tocPath = ShaderCacheReader.TocPathFor(full, DxbcStage.Vertex);
            var toc = f.Cache.ReadToc(tocPath);
            Assert.NotNull(toc);
            var doc = MaterialDocument.Parse(f.SkinBin, f.BinName, f.WadPath);
            var body = doc.Materials.First(m => m.Name.EndsWith("Locke_Body_inst", StringComparison.OrdinalIgnoreCase));
            var perms = new ShaderPermutationIndex(Final);
            perms.TryGetShaderDefs(body.RenderShader!, out var feat, out var swDef);
            var perm = ShaderCacheReader.ResolvePermutation(toc!, body.Macros, body.Switches, feat, swDef, out var why);
            Assert.True(perm is not null, why);
            var blob = f.Cache.LoadShader(tocPath, perm!.BlobIndex, out _);
            Assert.NotNull(blob);

            string[] lines = Disassemble(blob!.Bytecode).Split('\n').Select(l => l.Trim()).ToArray();
            var vcam = blob.ConstantBuffers.SelectMany(cb => cb.Variables.Select(v => (cb, v))).First(x => x.v.Name == "vCamera");
            string vcamReg = $"cb{vcam.cb.BindPoint}[{vcam.v.Offset / 16}]";
            // (skinned position) - vCamera, then normalised: the depth push direction.
            Assert.Contains(lines, l => l.StartsWith("add ", StringComparison.Ordinal) && l.Contains("-" + vcamReg + ".xyz"));
            // The mesh colour goes straight through as TEXCOORD0 (o1) - it is the shader's own input.
            int colorIn = (int)blob.Inputs.First(i => i.Semantic.Equals("COLOR", StringComparison.OrdinalIgnoreCase)).Register;
            Assert.Contains(lines, l => l.StartsWith($"mov o1.xyzw, v{colorIn}.xyzw", StringComparison.Ordinal));
            // A cbuffer position the shader multiplies by mProj comes AFTER the push, so mProj is what
            // takes a world position to clip - the full view-projection, not projection alone.
            var mproj = blob.ConstantBuffers.SelectMany(cb => cb.Variables.Select(v => (cb, v))).First(x => x.v.Name == "mProj");
            Assert.Contains(lines, l => l.StartsWith("dp4 o0.x", StringComparison.Ordinal) && l.Contains($"cb{mproj.cb.BindPoint}[{mproj.v.Offset / 16}]"));
        }
    }

    [Fact]
    public void TheRendererPosesBonesInWorldSpaceAndGivesTheShadersTheirOwnCamera()
    {
        var s = new PreviewSettings();
        Assert.Equal(BonePose.WorldTransposed, s.BonePose);

        // The camera the shaders see is the League-space one: the editor's camera sits on the display
        // side of the X mirror the view applies, so its shader-space position is the X-mirror of it.
        using var renderer = new ShaderPreviewRenderer();
        var supplied = new PreviewSettings { SuppliedCameraPosition = new Vector3(100f, 50f, 300f), MirrorX = true };
        Assert.Equal(new Vector3(-100f, 50f, 300f), renderer.ShaderCamera(supplied));
        var unmirrored = new PreviewSettings { SuppliedCameraPosition = new Vector3(100f, 50f, 300f), MirrorX = false };
        Assert.Equal(new Vector3(100f, 50f, 300f), renderer.ShaderCamera(unmirrored));

        var d3d = File.ReadAllText(Path.Combine(RepoRoot(), "src", "ReyEngine.Rendering.D3D11", "ShaderPreviewRenderer.cs"));
        Assert.Contains("\"MPROJ\" => Mat(NeedsViewProjection(mat, s) ? vp : proj, s)", d3d);
        Assert.Contains("bool transpose = s.BonePose is BonePose.ViewTransposed or BonePose.WorldTransposed;", d3d);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    // ===================================================== the picture

    [Fact]
    public void LockeDrawsWholeAtRest()
    {
        // Three renders of the same scene. As prepared - the file-order colour and the drivers' rest
        // values - against the colour forced white: the mask the shader reads must hide nothing at rest,
        // so the two cover the same pixels. And against the authored VCDissolve_Value put back: that is
        // the value the editor shows, and it dissolves the lower body, which is the picture this milestone
        // started from.
        if (Locke() is not { } f) return;
        using (f)
        {
            var scene = Dx11CharacterScene.Prepare(f.Skn, f.SkinBin, f.Cache, new ShaderPermutationIndex(Final),
                f.Read, f.BinName, f.WadPath, fallbackShader: Dx11CharacterScene.DefaultCharacterShader);
            Assert.NotNull(scene);
            var onsen = scene!.Slices.Where(sl => sl.Material.EndsWith("_inst", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(onsen.Count >= 5, "Onsen did not resolve: " + string.Join(" | ", scene.Failures));
            Assert.All(onsen, sl => Assert.False(sl.UsedFallbackShader, sl.Material + " took the stand-in shader"));
            Assert.All(onsen, sl => Assert.Contains("onsen", sl.VsDesc.ShaderName, StringComparison.OrdinalIgnoreCase));

            var mesh = SkinnedMeshDecoder.Decode(f.Skn);
            var centre = (mesh.BoundsMin + mesh.BoundsMax) * 0.5f;
            float radius = (mesh.BoundsMax - mesh.BoundsMin).Length() * 0.5f;
            var eye = centre + new Vector3(radius * 0.6f, radius * 0.1f, radius * 1.25f);
            var view = Matrix4x4.CreateLookAt(eye, centre, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 1f, radius * 0.02f, radius * 40f);
            const int Size = 320;

            long? Covered()
            {
                using var renderer = new ShaderPreviewRenderer();
                if (!renderer.Initialize(out _)) return null;
                Dx11CharacterScene.Commit(renderer, scene, "");
                var s = new PreviewSettings
                {
                    SuppliedView = view, SuppliedProjection = proj, SuppliedCameraPosition = eye,
                    AlphaBlend = true, DepthTest = true, MirrorX = true, TransposeMatrices = true,
                    CullBackFaces = true, SortByPipeline = true,
                    ClearColor = new Vector4(0.039f, 0.051f, 0.075f, 1f), TimeSeconds = 1.0f,
                };
                var frame = renderer.RenderFrame(Size, Size, s, out _);
                if (frame is null) return null;
                long covered = 0;
                for (int i = 0; i + 3 < frame.Length; i += 4)
                {
                    int a = frame[i], b = frame[i + 1], c = frame[i + 2];
                    if (Math.Abs(a - 10) <= 6 && Math.Abs(b - 13) <= 6 && Math.Abs(c - 19) <= 6) continue;   // the clear colour, either byte order
                    if (Math.Abs(a - 19) <= 6 && Math.Abs(b - 13) <= 6 && Math.Abs(c - 10) <= 6) continue;
                    covered++;
                }
                return covered;
            }

            var rest = Covered();
            if (rest is null) return;   // no device
            Assert.True(rest > 2000, $"the rest pose drew {rest} px");

            var authoredColours = scene.Mesh.Vertices.Select(v => v.Color).ToArray();
            for (int i = 0; i < scene.Mesh.Vertices.Length; i++) scene.Mesh.Vertices[i].Color = Vector4.One;
            var white = Covered();
            for (int i = 0; i < scene.Mesh.Vertices.Length; i++) scene.Mesh.Vertices[i].Color = authoredColours[i];
            Assert.NotNull(white);
            Assert.True(rest >= white * 0.97, $"the mask hides part of him at rest: {rest} px against {white} px with a white mask");

            foreach (var sl in scene.Slices)
                foreach (var (name, value) in sl.Parameters)
                    if (name == "VCDissolve_Value") value[0] = 1.23f;
            var authored = Covered();
            Assert.NotNull(authored);
            Assert.True(authored < rest * 0.92, $"the authored VCDissolve_Value did not dissolve anything: {authored} px against {rest} px at rest - the driver's rest value is not what separates the two pictures");
        }
    }
}
