using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Materials;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.DXGI;
// our namespace is itself called D3D11, which shadows Silk's API type of the same name
using SilkD3D11 = Silk.NET.Direct3D11.D3D11;

using ReyEngine.Core.Assets;
namespace ReyEngine.Rendering.D3D11;

/// <summary>Camera / lighting / render knobs the preview drives the shader with.</summary>
public sealed class PreviewSettings
{
    public float Yaw = 0.6f, Pitch = 0.4f, Distance = 3.2f;
    public float Fov = 0.9f;

    /// <summary>M215: when set, the renderer uses THESE instead of its own orbit maths.
    ///
    /// <para>The window drives an <c>OrbitCamera</c> - the same class and the same WASD/look/pan bindings as
    /// the map viewport - and hands the result down. The matrices are passed rather than the camera object
    /// so this assembly keeps no reference to the OpenGL renderer that owns it; the isolation is the point
    /// of the separate project. The built-in orbit stays as the fallback the headless harnesses use.</para></summary>
    public Matrix4x4? SuppliedView;
    public Matrix4x4? SuppliedProjection;
    public Vector3? SuppliedCameraPosition;
    public Vector3 SunDirection = Vector3.Normalize(new Vector3(-0.4f, -0.8f, -0.45f));
    public Vector4 SunColor = new(1f, 0.97f, 0.9f, 1f);
    public Vector4 ClearColor = new(0.08f, 0.09f, 0.11f, 1f);
    public float TimeSeconds;

    public bool Wireframe;
    public bool CullBackFaces = true;
    public bool DepthTest = true;
    public bool AlphaBlend;

    /// <summary>HLSL packs cbuffer matrices column-major by default, so a row-major
    /// <see cref="Matrix4x4"/> is conventionally transposed before upload. Riot's shaders were compiled
    /// with the default packing, but whether they do <c>mul(v, M)</c> or <c>mul(M, v)</c> is not recorded
    /// anywhere in the bytecode we read — so this is exposed as an A/B rather than guessed silently.
    /// If the mesh renders inside-out, squashed, or vanishes, flip this first.</summary>
    public bool TransposeMatrices = true;

    /// <summary>Draw with ReyEngine's own shading model instead of Riot's pixel shader, for the A/B.</summary>
    public bool UseComparisonShader;

    /// <summary>
    /// M661: the viewport's debug view, using the SAME numbering the OpenGL fragment shader's uMode
    /// uses - 0 Basic, 1 RiotApprox, 2..14 the debug views. Below
    /// <see cref="ShaderPreviewRenderer.FirstDebugMode"/> nothing happens and Riot's own shaders draw,
    /// which is what 0 and 1 mean here.
    /// </summary>
    public int DebugMode;

    /// <summary>M228: the map's own sun and lightmap values, when a map scene supplied them. Null falls
    /// back to the UI sliders and a neutral scale.</summary>
    public Vector4? MapSunColor;
    public Vector3? MapSunDirection;
    public float? MapLightMapScale;

    /// <summary>M229: the map's depth-fog colour and its RAW fogStartAndEnd, in Riot's own convention -
    /// the shader consumes them unmodified, so they must NOT be normalised to (near, far) here.</summary>
    public Vector4? MapFogColor;
    public Vector2? MapFogStartEnd;

    /// <summary>
    /// M463: the whole authored sun record, for the two inputs that are not single constants -
    /// <c>LightRegionInfo_SharedDataBuffer</c> (a 112-byte structured buffer, see
    /// <see cref="ReyEngine.Formats.Lighting.LightRegionInfoBuilder"/>) and the IBL ambient pair.
    ///
    /// <para>Added ALONGSIDE the granular <c>MapSun*</c> fields above rather than replacing them: those are
    /// consumed by name in <c>FillConstantBuffer</c> and each has its own null-means-neutral fallback that
    /// predates this. Both call sites already hold the record, so carrying it costs nothing.</para>
    ///
    /// <para><b>Which FORM arrives here matters.</b> The view-model hands over its RENDER form
    /// (<c>CurrentSunProperties</c>), in which M451 has already folded the intensity slider into
    /// <c>SunColor</c> and pinned <c>SunIntensityScale</c> to 1. The packer multiplies the two anyway, so it
    /// is correct for the folded form (x1) AND for a raw authored record - which is what makes it safe to
    /// unit-test against the values a bin actually carries.</para>
    /// </summary>
    public ReyEngine.Formats.MapGeo.MapSunProperties? MapSun;

    /// <summary>M452: the editor's dynamic point lights (the Lighting window list), for the additive
    /// overlay pass. The list and every knob below mirror the GL viewport's bindings one for one
    /// (ViewportControl.DynamicLights*), and the same clamps are applied at draw time - see
    /// <see cref="ShaderPreviewRenderer.DrawDynamicLights"/>.</summary>
    public IReadOnlyList<ReyEngine.Formats.Lighting.PointLight>? DynamicLights;
    public bool DynamicLightsEnabled;
    public float DynamicLightIntensity = 1f;
    public float DynamicLightRadiusScale = 1f;
    public float DynamicLightFalloffSoftness;
    public float DynamicLightPositionScale = 1f;
    public Vector2 DynamicLightPositionScaleXZ = Vector2.One;
    public Vector2 DynamicLightPositionOffset = Vector2.Zero;

    /// <summary>
    /// M395: GRASS_INTERP - the environment-transition crossfade factor, 0 = GRASS_TINT_MAP (the state
    /// being left) and 1 = GRASS_TINT_MAP_ALTERNATE (the state being entered).
    ///
    /// <para>Riot's own shaders do the blend; staticmesh/vertexdeform.ps.dx11 blob 19 ends with
    /// <c>mad r1, cb1[16].yyyy, (alt - base), base</c>, and cb1[16].y is PerFramePixelCB offset 260 =
    /// GRASS_INTERP. So supplying this constant IS the transition on the D3D11 side - there is nothing
    /// to reimplement.</para>
    ///
    /// <para>Defaults to 0, which is the correct resting value: with both slots bound to the same
    /// texture (the no-transition case) any factor is a no-op, and with two different ones 0 means
    /// "still showing the current state".</para>
    /// </summary>
    public float GrassInterp;

    /// <summary>M246: collapse pipeline state changes by drawing depth-writing geometry grouped by
    /// pipeline. Order-sensitive draws (additive, or anything that does not write depth) are never
    /// reordered. On by default; a toggle exists because if an ordering artefact ever does appear, being
    /// able to switch this off is how it gets identified rather than guessed at.</summary>
    public bool SortByPipeline = true;

    /// <summary>
    /// M460: bind Riot's glow buffer as a second render target, run their bloom chain on it and composite
    /// it back with their screen blend.
    ///
    /// <para>On by default because it is not an added effect: the shaders ReyEngine already runs compute
    /// the glow into <c>SV_Target1</c> every frame, and with one render target bound it was discarded
    /// (frame-pipeline.md §3.3). Off is the A/B - with the whole chain being three full-screen passes over
    /// an output nobody has seen before, being able to switch it off is how a regression gets attributed
    /// rather than guessed at.</para>
    /// </summary>
    public bool Bloom = true;

    /// <summary>
    /// M465: render the map from the sun into a depth map and let Riot's own PCF read it.
    ///
    /// <para>On by default, for the same reason bloom is: the five <c>SampleCmpLevelZero</c> taps are
    /// already executing in every environment pixel shader ReyEngine runs, against a 1x1 white stand-in, so
    /// this supplies an input rather than adding an effect. Off is the A/B - the expected gain is bounded
    /// (a baked map's shader takes <c>min(pcf, baked.w)</c>, so the bake's own static mask already darkens
    /// static geometry and this mostly adds props and fixes stale bakes), and bias artefacts are the kind
    /// of thing only an eye can judge, which needs a switch.</para>
    /// </summary>
    public bool Shadows = true;

    /// <summary>M223: mirror world X, which is what the rest of the editor's viewport does. League's data is
    /// authored in the opposite handedness to the renderer, so without this a map is laid out mirrored
    /// against every other view in the app.</summary>
    public bool MirrorX = true;

    /// <summary>M216: what the bone palette should contain.
    ///
    /// <para>M646: object-to-WORLD, and <c>mProj</c> is then the full view-projection. M216 read the
    /// bytecode as "mProj used, mView unused, so the bones must carry object-to-view and mProj is
    /// projection alone" - a reading under which the picture is geometrically identical, which is why it
    /// held for 430 milestones. It is refuted by what the vertex shaders DO with the skinned position:
    /// skinnedmesh/onsen computes <c>normalize(pos - vCamera)</c> for its depth push and builds its fog-of-war
    /// UV from <c>pos.xz * FOG_OF_WAR_PARAMS</c>, a world-space map; and the particle path had already
    /// measured vCamera as a world-space camera (M231, quad_vs). A world-space camera subtracted from a
    /// view-space position is nonsense in Riot's own engine, so the position is world-space and the bones
    /// carry object-to-world. Under the old reading every constant that reads the skinned position as
    /// world - the view vector behind every fresnel, the fog-of-war lookup, the depth push - was wrong,
    /// and Onsen, whose whole look is fresnel, was the first shader where it showed.</para>
    ///
    /// <para>ViewTransposed is kept as the A/B against the old reading.</para></summary>
    public BonePose BonePose = BonePose.WorldTransposed;

    /// <summary>M615: per-bone skinning matrices, indexed by influence slot — what
    /// <c>Formats.Animation.BonePalette.Build</c> produces. Null keeps the M216 behaviour of one constant
    /// matrix in every slot, which is the bind pose and is what everything that is not an animated
    /// character wants.
    ///
    /// <para>Object-to-object, NOT pre-multiplied by the view: the renderer folds the view in, because it
    /// is the only place that knows which view this draw is using and a capture renders the same palette
    /// from a different camera.</para></summary>
    public Matrix4x4[]? BonePalette;

    /// <summary>M620: where the subject stands. Identity for every existing caller.
    ///
    /// <para>For a SKINNED mesh this has to reach the bone palette rather than a world constant: Riot's
    /// character vertex shaders transform a vertex by its bone matrices and nothing else, so a world
    /// matrix written anywhere else is a matrix no character shader reads. Folded in as
    /// <c>skin * world * view</c>, which is still exactly <c>view</c> when both are identity.</para></summary>
    public Matrix4x4 World = Matrix4x4.Identity;

    /// <summary>Rows per bone matrix in <c>BonesCB</c>. League caps skeletons at 256 bones and the buffer is
    /// 12,288 bytes, which is 48 bytes each at 3 rows (a 4x3) or 64 at 4 (a full 4x4 over 192 bones). Both
    /// divide evenly, so this is measured in the app rather than assumed - a wrong stride makes the skeleton
    /// shear instead of vanishing, which is easy to mistake for a broken mesh.</summary>
    public int BoneMatrixRows = 3;
}

/// <summary>Result of trying to bring a shader pair up — every failure carries its own message.</summary>
/// <summary>M216: candidate contents for the bone palette, kept switchable because the packing is not
/// recorded anywhere in the bytecode we read.</summary>
public enum BonePose
{
    /// <summary>Bind pose. Correct only if the shader applies a view matrix itself.</summary>
    Identity,
    /// <summary>The view matrix, rows as-is.</summary>
    View,
    /// <summary>The view matrix transposed - the layout a float4x3 takes under HLSL's default packing.</summary>
    ViewTransposed,
    /// <summary>M646: the model transform alone (object-to-world), transposed into the float4x3 layout.
    /// The shader's <c>mProj</c> then has to be the full view-projection - see BonePose above.</summary>
    WorldTransposed,
}

/// <summary>Per-material 2D texture addressing derived from the authored sampler value.</summary>
public enum PreviewSamplerAddress
{
    Wrap,
    ClampU,
    ClampV,
    ClampUV,
}

public sealed class ShaderLoadReport
{
    public bool Success;
    public string? Error;
    public readonly List<string> Steps = new();
    public readonly List<string> Warnings = new();
    /// <summary>Vertex-shader input elements the fat vertex has no real data for.</summary>
    public readonly List<string> UnmatchedInputs = new();
    /// <summary>Reflected textures with nothing bound — these sample as opaque white.</summary>
    public readonly List<string> UnboundTextures = new();
    /// <summary>Reflected, USED constants nothing filled in — they upload as zero.</summary>
    public readonly List<string> UnboundConstants = new();

    public void Step(string s) => Steps.Add(s);
    public void Warn(string s) => Warnings.Add(s);
}

/// <summary>M214: one material's whole pipeline — its two shaders, the input layout generated from them,
/// its constant buffers, its textures, and the slice of the index buffer it draws.
///
/// <para>A scene is a list of these. A champion skin is one mesh whose submeshes each name a different
/// material, so drawing it correctly means a separate shader, permutation and texture set per submesh —
/// exactly what the single-material bench could not express.</para></summary>
public sealed unsafe class PreviewMaterial : IDisposable
{
    public required string Name { get; init; }
    public required DxbcShader VsRefl { get; init; }
    public required DxbcShader PsRefl { get; init; }

    internal ComPtr<ID3D11VertexShader> Vs;
    internal ComPtr<ID3D11PixelShader> Ps;
    internal ComPtr<ID3D11InputLayout> Layout;
    internal readonly Dictionary<int, ComPtr<ID3D11Buffer>> VsCbs = new();
    internal readonly Dictionary<int, ComPtr<ID3D11Buffer>> PsCbs = new();
    internal readonly Dictionary<string, ComPtr<ID3D11ShaderResourceView>> Textures =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>M661: the same textures again, under what the debug views call them. Filled beside
    /// <see cref="Textures"/> so the debug pass can bind a diffuse or a lightmap to a fixed register
    /// without knowing this material's own register layout - which is what lets one generated debug
    /// shader serve every material that shares a vertex signature.</summary>
    internal readonly Dictionary<DebugSlot, ComPtr<ID3D11ShaderResourceView>> DebugTextures = new();

    /// <summary>Values this material authors, e.g. its own TintColor. Beat the renderer's engine values.</summary>
    public Dictionary<string, float[]> Params { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which slice of the shared index buffer this material covers. IndexCount &lt; 0 means "all".</summary>
    public int StartIndex { get; set; }
    public int IndexCount { get; set; } = -1;

    public bool Visible { get; set; } = true;

    /// <summary>Use the StaticMaterialDef pass's authored color blend factors instead of the generic
    /// SrcAlpha/InvSrcAlpha preview state. This is how Map22 shadow receivers multiply over the textured
    /// arena beneath them rather than covering it with their white helper texture.</summary>
    public bool UsesAuthoredColorBlend { get; set; }
    public MaterialBlendFactor SourceColorBlend { get; set; } = MaterialBlendFactor.One;
    public MaterialBlendFactor DestinationColorBlend { get; set; } = MaterialBlendFactor.Zero;

    /// <summary>M292: the mapgeo GROUP this material was built from, or -1 for anything that is not map
    /// geometry - particles, mesh emitters, editor overlays. The host uses it to drive
    /// <see cref="Visible"/> from the very same per-group array the OpenGL viewport consumes, so the two
    /// viewports cannot disagree about dragon layers, baron state or render regions. The -1 default is
    /// what keeps a blanket visibility sweep from stomping particle materials that manage their own.</summary>
    public int MapGroupIndex { get; set; } = -1;

    /// <summary>M661: the source mapgeo mesh for this group has a negative-determinant (mirrored)
    /// transform. Not derivable here - the merged vertex buffer has every transform baked in and the
    /// per-mesh fact is gone by the time geometry reaches this renderer - so the host publishes it by
    /// group, out of the SAME per-group array the OpenGL viewport reads. Only the Mirrored debug view
    /// uses it.</summary>
    public bool SourceMirrored { get; set; }

    /// <summary>M264: read this draw's geometry from the renderer's DYNAMIC buffer rather than the static
    /// scene mesh. Set by anything that rewrites its vertices every frame - particles today.</summary>
    public bool UsesDynamicMesh { get; set; }

    /// <summary>M707: the base (TEXTURE) slot holds the engine's 1x1 transparent texel because the emitter
    /// names no texture - neither a real sprite nor a placeholder. Only the heat-haze pass has to know: its
    /// "ships no diffuse" test was a null handle, and a bound transparent texel is not null.</summary>
    public bool BaseTextureIsUnnamed { get; set; }

    /// <summary>M266: false for particles. GL runs them with the depth TEST on and the depth MASK off
    /// (VfxParticleRenderer.cs:350-351); the single global depth state here writes depth unconditionally, so
    /// without this an additive quad occludes the map behind it. Everything else leaves this true and gets
    /// byte-identical behaviour.</summary>
    /// <remarks>M720: false for every particle mode but NONE, which is the one the engine writes depth for.</remarks>
    public bool WritesDepth { get; set; } = true;

    /// <summary>
    /// M717: per-slot address mode, by sampler name, in Riot's own enum - 0 Wrap, 1 Clamp, 2 Mirror.
    ///
    /// <para><see cref="ClampedSamplers"/> answers "this slot is a lookup table", which is a yes or no.
    /// A stage that carries an AUTHORED address mode needs all three answers, and the erosion map is the
    /// first: its coordinate is the base texture's own atlas position with the scroll added, so it leaves
    /// the map constantly and what happens there is the artist's choice. Null leaves every slot on the
    /// material's own mode, which is what everything but the erosion still does.</para></summary>
    public Dictionary<string, int>? SlotAddress { get; set; }

    /// <summary>M711: false for a particle whose <c>miscRenderFlags</c> carries the engine's DISABLE_ZBUFFER
    /// bit - it draws over everything instead of being occluded. Only the test moves; the write is already
    /// off for every particle through <see cref="WritesDepth"/>. Everything else leaves this true.</summary>
    public bool TestsDepth { get; set; } = true;

    /// <summary>Address mode for the material's ordinary texture samplers. Explicit clamp samplers and
    /// comparison samplers still take precedence when the shader declares them.</summary>
    public PreviewSamplerAddress SamplerAddress { get; set; }

    /// <summary>M635: sampler slots (by reflected name, e.g. <c>sPalettesTexture__SMP</c>) that must CLAMP
    /// whatever <see cref="SamplerAddress"/> says for the rest of the material. A particle's sprite has to
    /// wrap - flipbooks scroll and multipliers drift - while its palette strip is a lookup table: under WRAP
    /// with linear filtering, a lookup at u = 0 (every black texel of an additive sprite) blends the strip's
    /// first texel with its LAST, and Aatrox's AA_Gradient_RGB runs from (51,0,54) to (249,249,134), so the
    /// whole black field of the sprite came out (150,125,94) grey and drew as a rectangle. GL has bound a
    /// clamp sampler to this slot since M184 for the same reason; this is the per-slot mechanism that path
    /// needed and did not have.</summary>
    public HashSet<string>? ClampedSamplers { get; set; }

    /// <summary>M246: which distinct pipeline this material uses. Materials sharing an id share their
    /// shaders and input layout, so drawing them back to back costs no state change. -1 = uncached, which
    /// sorts last and keeps its relative order.</summary>
    public int PipelineId { get; set; } = -1;

    /// <summary>M245: this slice's world-space bounds, for frustum culling. Null means "always draw" -
    /// which is what a single-mesh preview, a particle system, or anything whose extent is not known
    /// should get, because culling on a guess is worse than not culling.</summary>
    public (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)? Bounds { get; set; }

    /// <summary>M354: the material's authored cullEnable - true means Riot marked this surface
    /// single-sided and the game culls its back faces. Default false keeps every existing caller
    /// (particles, props, champion skins) exactly as it was; only the map builder sets it.</summary>
    public bool CullBackFaces { get; set; }

    /// <summary>M363: this material's emitter authored softParticleParams, so it needs the scene depth
    /// snapshot bound rather than the white stand-in. Set by the emitter pipeline; false leaves the
    /// pre-M363 behaviour, which is a fade neutralised to fully visible rather than anything broken.</summary>
    public bool NeedsSceneDepth { get; set; }

    /// <summary>M232: the material adds to what is behind it. Since M720 a particle's real state is
    /// <see cref="ParticleBlend"/>; this summary survives for the non-particle paths and the shadow test.</summary>
    public bool Additive { get; set; }

    /// <summary>M720: a particle emitter's blend, from the engine's own enum (VfxBlend). Non-null takes
    /// precedence over <see cref="Additive"/> at every particle draw site; null is every non-particle material.</summary>
    public ReyEngine.Formats.Vfx.VfxBlendState? ParticleBlend { get; set; }

    /// <summary>M720: cSoftParticleControl for this emitter's mode - three distinct vectors over nine modes,
    /// and the shared constant-buffer key splits on it. Null falls back to the pre-M720 additive/alpha pair.</summary>
    public Vector4? ParticleSoftControl { get; set; }

    /// <summary>M282: non-null makes this a heat-haze draw - the renderer replaces the material's own
    /// shaders with the distortion pipeline and refracts the scene behind the quad instead of shading it.
    /// The value is the authored <c>distortionDefinition.distortion</c> strength.
    ///
    /// <para>Distortion deliberately does NOT ride on <see cref="Additive"/>. Riot authors these emitters
    /// blendMode=1, which reads as additive, and additive on top of an already-bright refracted sample is
    /// what turns heat haze into a white blob - so GL overrides the authored mode back to straight alpha
    /// (VfxParticleRenderer.cs:398-402) and this does the same.</para></summary>
    public float? DistortionStrength { get; set; }

    /// <summary>M283: non-null makes this a MESH-primitive emitter - it draws a real .skn through the mesh
    /// pipeline instead of a billboard out of the shared quad buffer. The value is a handle from
    /// <see cref="ShaderPreviewRenderer.CreateMeshGeometry"/>.</summary>
    public int? MeshGeometryId { get; set; }

    /// <summary>M364: non-null makes this a BEAM or TRAIL emitter - it draws a world-space ribbon strip
    /// through the ribbon pipeline instead of a billboard. The value is a handle from
    /// <see cref="ShaderPreviewRenderer.CreateRibbon"/>, and the strip itself is re-uploaded every frame
    /// because it is extruded from particle history that moves.</summary>
    public int? RibbonId { get; set; }

    /// <summary>The emitter's live particles, in the simulator's packed layout
    /// (<see cref="ShaderPreviewRenderer.MeshInstanceStride"/> floats each). Handed over by reference and
    /// re-read every frame rather than copied.</summary>
    public float[]? MeshInstances { get; set; }
    public int MeshInstanceCount { get; set; }

    /// <summary>The placement's basis, already normalised. GL discards the placement's scale by
    /// normalising these, so only its rotation reaches the mesh - matching that matters more than being
    /// right, or the two viewports disagree about how big a door shield is.</summary>
    public Vector3 MeshRight { get; set; } = Vector3.UnitX;
    public Vector3 MeshUp { get; set; } = Vector3.UnitY;
    public Vector3 MeshForward { get; set; } = Vector3.UnitZ;

    public Vector2 MeshUvOffset { get; set; }
    public Vector2 MeshUvOffsetMult { get; set; }
    public Vector2 MeshTexDiv { get; set; } = Vector2.One;
    public Vector2 MeshTexDivMult { get; set; } = Vector2.One;

    /// <summary>Back-face culling for this emitter, from <c>!disableBackfaceCull</c>. The mesh path is the
    /// only place in the VFX renderer that culls at all.</summary>
    public bool MeshCull { get; set; }

    /// <summary>M295: prop placements, as real world matrices. Non-null selects the PROP branch of the
    /// mesh draw: the particle instance fields are neutralised and this matrix is the whole transform.
    /// A prop's placement is an arbitrary 4x4 straight out of the map's .materials.bin - rotation,
    /// non-uniform scale, shear - which the particle layout (position + one scalar scale + a Y spin)
    /// simply cannot represent.</summary>
    public IReadOnlyList<Matrix4x4>? MeshModels { get; set; }
    /// <summary>Per-particle world matrices for constrained geometry such as a tether mesh.
    /// Unlike prop MeshModels, these retain each particle's colour and erosion drive.</summary>
    public IReadOnlyList<Matrix4x4>? MeshParticleTransforms { get; set; }

    /// <summary>M295: which slice of the mesh's index buffer this material draws. A prop's submeshes each
    /// carry their own diffuse, so one uploaded geometry is drawn by several materials over different
    /// ranges. 0/0 means "all of it", which is what every particle emitter wants.</summary>
    public int MeshIndexStart { get; set; }
    public int MeshIndexCount { get; set; }

    /// <summary>M297: discard texels below this alpha. 0 disables it, which is what every particle wants -
    /// they blend. Props need it because they WRITE depth, so a blended fringe stamps depth for texels
    /// that are visually absent and halos whatever is behind.</summary>
    public float MeshAlphaCutoff { get; set; }

    /// <summary>M640: a mesh-primitive emitter drawn through RIOT'S mesh shaders (particlesystem/mesh_vs +
    /// mesh_ps) rather than the ported MeshHlsl. The material's own pipeline, textures, constants and
    /// stages apply exactly as they do to a quad; only the final draw differs - the geometry lives in
    /// <see cref="ShaderPreviewRenderer.CreateRiotMeshGeometry"/>'s store and is drawn once per particle
    /// with <c>mWorld</c> and <c>kColorFactor</c> re-issued per instance, which is how the shader takes
    /// them (mesh_vs blob 0: <c>dp4 r1, pos, cb3[0..3]</c> and <c>mov o1, cb1[7]</c>). Mutually exclusive
    /// with <see cref="MeshGeometryId"/>.</summary>
    public int? RiotMeshGeometryId { get; set; }

    /// <summary>M676: a skinning palette of this material's own, for a skinned mesh that is not THE mesh -
    /// a placed prop drawn through Riot's character shaders. Null reads the frame's
    /// <see cref="PreviewSettings.BonePalette"/> as before, which is what the character window sets.</summary>
    public Matrix4x4[]? BonePalette { get; set; }

    /// <summary>M676: draw this material once per world transform - the placements of a prop. Needs
    /// <see cref="RiotMeshGeometryId"/> to point at a SKINNED geometry (the PreviewMesh overload of
    /// <see cref="ShaderPreviewRenderer.CreateRiotMeshGeometry(PreviewMesh)"/>). The bones and mWorld
    /// constants are refilled per placement, and a material with this set never shares a constant buffer -
    /// see ResolveCb.</summary>
    public IReadOnlyList<Matrix4x4>? CharacterInstances { get; set; }

    /// <summary>M680: per placement, the LIGHTGRID_COLORS a placed prop is lit by - the map's lightgrid
    /// sampled where it stands - aligned with <see cref="CharacterInstances"/>. A null entry, or no list,
    /// leaves that placement on the neutral stand-in the character window uses.</summary>
    public IReadOnlyList<float[]?>? CharacterInstanceAmbient { get; set; }

    /// <summary>The key <see cref="Textures"/> holds a distortion emitter's normal map under. Reserved
    /// rather than a real sampler name because no shader in Riot's cache declares this stage - it belongs
    /// to our own pipeline. Routed through the ordinary texture pool so its lifetime is pooled like every
    /// other view (see the ownership note below); materials do not own their SRVs.</summary>
    public const string DistortionNormalKey = "__DISTORT_NORMAL";

    /// <summary>M246: safe to reorder relative to other draws. True only when this draw WRITES DEPTH and
    /// is not additive - such draws resolve by the depth buffer, so submission order is not observable.
    /// An additive or non-depth-writing draw blends with whatever is already there, so its order IS the
    /// image and must be preserved.</summary>
    public bool SortableByPipeline { get; set; }

    /// <summary>M456: this material's pixel shader carries Riot's own clustered light loop, so the lights
    /// are evaluated INSIDE it and the M452 additive overlay must skip the slice.
    ///
    /// <para>Derived from the BYTECODE, not from what the scene builder intended to select. A permutation
    /// that declares <c>CLUSTER_MAP_SharedTexture</c> has the loop compiled in whether we asked for it or
    /// not, and the same fact has to decide both "upload the cluster data" and "skip the overlay" or a
    /// surface gets lit twice. Cached because it is asked once per material per frame.</para></summary>
    public bool UsesClusterLighting => _usesClusterLighting ??= PsRefl.Resources.Any(r =>
        r.Name.Equals(ClusterMapTextureName, StringComparison.OrdinalIgnoreCase));
    private bool? _usesClusterLighting;

    /// <summary>The reflected name that identifies a cluster-lit permutation. One spelling, one place.</summary>
    public const string ClusterMapTextureName = "CLUSTER_MAP_SharedTexture";

    /// <summary>Whether anything is bound under this key. The texture dictionary itself is internal - the
    /// views in it are pool-owned and handing them out invites a caller to dispose one - but a material's
    /// builder legitimately needs to know whether an OPTIONAL stage resolved, which is a question about the
    /// binding and not about the view.</summary>
    public bool HasTexture(string key) => Textures.ContainsKey(key);

    public IEnumerable<string> UnboundTextures =>
        PsRefl.Textures.Concat(VsRefl.Textures).Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !Textures.ContainsKey(n));

    /// <summary>M226: the texture views are NOT owned here. They live in the renderer's pool, shared by
    /// every material that binds the same asset - a Map12 load binds 1,841 slots from 138 distinct files,
    /// and one lightmap atlas is shared by 282 of them. Disposing per material would release a view other
    /// materials are still using, which blanks textures or crashes.</summary>
    /// <summary>M242: false when Vs/Ps/Layout came from the pipeline cache and are shared with other
    /// materials. Disposing a shared shader object is a use-after-free that shows up as a device removal
    /// on some later frame, a long way from the cause - so ownership is explicit rather than assumed.</summary>
    public bool OwnsPipeline { get; set; } = true;

    public void Dispose()
    {
        if (OwnsPipeline) { Vs.Dispose(); Ps.Dispose(); Layout.Dispose(); }
        // Constant buffers are ALWAYS per-material: they hold this material's uploaded values, so two
        // materials sharing a pipeline still need their own. Only the immutable objects are shared.
        foreach (var b in VsCbs.Values) b.Dispose();
        foreach (var b in PsCbs.Values) b.Dispose();
        VsCbs.Clear(); PsCbs.Clear(); Textures.Clear();
    }
}

/// <summary>M210: an isolated Direct3D 11 renderer that runs Riot's own compiled shaders.
///
/// <para><b>Why offscreen + readback rather than a swapchain.</b> Avalonia offers <c>OpenGlControlBase</c>
/// and no D3D11 equivalent. Presenting a real swapchain means either a <c>NativeControlHost</c> child HWND,
/// which sits above the Avalonia compositor and would draw over every toolbar and overlay in the window, or
/// shared-texture interop back into the GL context. Both were identified as the deciding cost in the D3D11
/// spike. This renderer sidesteps both: it draws into an offscreen render target, copies to a staging
/// texture, and hands back BGRA bytes the UI blits into a normal bitmap. Slower than a swapchain and
/// completely irrelevant at preview sizes, with no compositor conflict at all.</para>
///
/// <para>Everything about the pipeline is driven by what the shader's own DXBC declares — constant-buffer
/// slots and byte offsets, texture and sampler registers, and the vertex input signature. Nothing is
/// hardcoded per shader, because nothing is known per shader.</para>
/// </summary>
public sealed unsafe partial class ShaderPreviewRenderer : IDisposable
{
    private static readonly float[] NeutralIblCubemapScales = Repeat(new[] { 1f, 1f, 1f, 1f }, 32);

    private SilkD3D11? _d3d;
    private ComPtr<ID3D11Device> _device;
    private ComPtr<ID3D11DeviceContext> _ctx;

    private ComPtr<ID3D11PixelShader> _comparePs;

    // M269: the overlay pipeline - our own shaders, for things Riot's have no way to express. Editor
    // furniture (a selection highlight, and later bounds/bones/buckets) is not something a game shader
    // was ever asked to draw, so it gets a trivial pipeline of its own rather than a contorted material.
    private ComPtr<ID3D11VertexShader> _overlayVs;
    private ComPtr<ID3D11PixelShader> _overlayPs;
    private ComPtr<ID3D11InputLayout> _overlayLayout;
    private ComPtr<ID3D11Buffer> _overlayCb;
    private ComPtr<ID3D11DepthStencilState> _overlayDepth, _overlayDepthNoTest;
    private ComPtr<ID3D11BlendState> _overlayBlend;
    private bool _overlayTried;
    private List<(int Start, int Count)> _highlight = new();
    private object? _highlightSource;

    // M270/M305: placement markers - particles, sounds, props, probes, lights. Their own buffer pair, because the
    // dynamic pair belongs to the particle simulation and both are rewritten every frame.
    private ComPtr<ID3D11Buffer> _iconVb, _iconIb;
    private int _iconVbCapacity, _iconIbCapacity;
    private readonly List<(Vector3 Pos, Vector4 Color, float Size, ViewportIcon Glyph)> _icons = new();
    private object? _iconSource;
    private PreviewVertex[] _iconVertsCpu = Array.Empty<PreviewVertex>();
    private uint[] _iconIndicesCpu = Array.Empty<uint>();
    private ComPtr<ID3D11VertexShader> _overlayVsTex;
    private ComPtr<ID3D11PixelShader> _overlayPsTex;
    private ComPtr<ID3D11InputLayout> _overlayLayoutTex;
    private ComPtr<ID3D11SamplerState> _iconSampler;
    private readonly ComPtr<ID3D11ShaderResourceView>[] _glyphSrv = new ComPtr<ID3D11ShaderResourceView>[5];
    private ComPtr<ID3D11Buffer> _vb, _ib;

    /// <summary>M381: per-triangle EDGE indices, so the selection highlight can be drawn as GL draws it -
    /// a wireframe outline rather than a translucent fill. Triangle t occupies [t*6, t*6+6), so a submesh
    /// index range [Start, Start+Count) maps to wire range [Start*2, Count*2) - the same arithmetic
    /// ViewportMeshRenderer uses against its _wireEbo, deliberately, so the two cannot drift.</summary>
    private ComPtr<ID3D11Buffer> _wireIb;
    // M264: a SECOND pair, for geometry that is rewritten every frame. Until now SetMesh and
    // SetDynamicMesh both replaced _vb/_ib, so a scene could hold static geometry or particles but never
    // both - which is why the map viewport could not show particles at all.
    private ComPtr<ID3D11Buffer> _dynVb, _dynIb;
    private int _dynIndexCount;
    private int _indexCount;

    // M282: the distortion (heat haze) pass - our own pipeline, for the same reason the overlay is one.
    // Riot's quad_ps has no distortion permutation to select: distortion is a separate screen-space stage
    // in the real engine, not a flag on the billboard shader, so there is nothing in the shader cache that
    // could draw it. See the GL original at VfxParticleRenderer.cs:1631-1640, which this ports exactly.
    private ComPtr<ID3D11VertexShader> _distortVs;
    private ComPtr<ID3D11PixelShader> _distortPs;
    private ComPtr<ID3D11InputLayout> _distortLayout;
    private ComPtr<ID3D11Buffer> _distortCb;
    private bool _distortTried;

    // M283: mesh-primitive particle emitters. A separate pipeline again, and separate GEOMETRY - these
    // draw a real .skn, not a billboard, so they cannot live in the shared quad buffer the way every other
    // particle does. Ported from the GL mesh program (VfxParticleRenderer.cs:891-1023, 1310-1413).
    // M293: the bucket grid. Its own buffer and pipeline because the payload is a raw pos3+bary3 float
    // array (6 floats/vertex) straight from the view-model, not the fat PreviewVertex the scene and the
    // overlay share - so it cannot ride either of their input layouts.
    private ComPtr<ID3D11VertexShader> _gridVs;
    private ComPtr<ID3D11PixelShader> _gridPs;
    private ComPtr<ID3D11InputLayout> _gridLayout;
    private ComPtr<ID3D11Buffer> _gridVb;
    private int _gridVertexCount, _gridVbCapacity;

    // M569: the navgrid flag layers and the face-edit selection. Both existed only in the GL viewport,
    // so in DX11 mode - which is where the reporter works - neither drew anything at all. Same soup, same
    // grid pipeline, their own buffers because all three can be on together.
    private ComPtr<ID3D11Buffer> _navVb;
    private int _navVertexCount, _navVbCapacity;
    private (int Start, int Count, Vector4 Color)[] _navLayers = Array.Empty<(int, int, Vector4)>();
    private ComPtr<ID3D11Buffer> _faceVb;
    private int _faceVertexCount, _faceVbCapacity;
    private bool _gridTried;

    private ComPtr<ID3D11VertexShader> _meshVs;
    private ComPtr<ID3D11PixelShader> _meshPs;
    private ComPtr<ID3D11InputLayout> _meshLayout;
    private ComPtr<ID3D11Buffer> _meshCb;
    private ComPtr<ID3D11RasterizerState> _meshCullCw, _meshCullCcw;
    private bool _meshTried;

    /// <summary>One emitter's mesh. The vertex buffer is dynamic because an animated emitter re-skins on
    /// the CPU every frame and rewrites its positions; the index buffer never changes.</summary>
    private sealed class MeshGeom
    {
        public ComPtr<ID3D11Buffer> Vb, Ib;
        public int VertexCount, IndexCount;
        public float[] Interleaved = Array.Empty<float>();
    }
    private readonly List<MeshGeom?> _meshGeoms = new();

    /// <summary>Floats per mesh vertex: position (3) + uv (2). No normal - the ported shader has no
    /// lighting term that would read one. Fresnel and the reflection cubemap, which are the only things in
    /// the GL mesh shader that use normals, are deliberately not ported yet (see MeshHlsl).</summary>
    public const int MeshVertexStride = 5;

    private ComPtr<ID3D11Texture2D> _rt, _stage, _depth;
    private ComPtr<ID3D11RenderTargetView> _rtv;
    private ComPtr<ID3D11DepthStencilView> _dsv;
    /// <summary>M282: an immutable copy of the colour target, taken before the first distortion draw.
    /// Refraction has to read the scene it is refracting, and a shader may not sample the render target it
    /// is writing - so the pixels have to come from somewhere else. GL hits the identical constraint and
    /// solves it the identical way (VfxParticleRenderer.cs:192-217).</summary>
    private ComPtr<ID3D11Texture2D> _sceneCopy;
    private ComPtr<ID3D11ShaderResourceView> _sceneCopySrv;
    // M363: the soft-particle depth snapshot. Same copy-then-sample shape as the scene colour above.
    private ComPtr<ID3D11Texture2D> _depthCopy;
    private ComPtr<ID3D11ShaderResourceView> _depthCopySrv;
    private int _width, _height;

    private ComPtr<ID3D11SamplerState> _linearWrap, _linearClampU, _linearClampV, _linearClamp, _comparison;
    /// <summary>M717: the third of Riot's three address modes. Their enum is 0 Wrap, 1 Clamp, 2 Mirror -
    /// measured off their own named shared samplers in M184 - and the erosion map's declared default is
    /// the mirror, which four fifths of the emitters that author an erosion take by leaving the field
    /// out. Until this state existed every one of them sampled as a wrap.</summary>
    private ComPtr<ID3D11SamplerState> _linearMirror;
    /// <summary>M465: the comparison sampler used while a real shadow map is bound. See CreateStaticStates
    /// for why the pair exists rather than one state.</summary>
    private ComPtr<ID3D11SamplerState> _comparisonLessEqual;
    private ComPtr<ID3D11RasterizerState> _raster;
    private ComPtr<ID3D11BlendState> _blend, _blendOpaque;
    private ComPtr<ID3D11DepthStencilState> _depthState;
    /// <summary>M266: the same state with DepthWriteMask.Zero, selected per material by
    /// <see cref="PreviewMaterial.WritesDepth"/>. Particles need it; nothing else does.</summary>
    private ComPtr<ID3D11DepthStencilState> _depthStateNoWrite;
    /// <summary>M711: neither tested nor written - the DISABLE_ZBUFFER particle.</summary>
    private ComPtr<ID3D11DepthStencilState> _depthStateNoTest;

    /// <summary>The scene. One entry for the single-shader bench, one per submesh for a loaded model.</summary>
    private readonly List<PreviewMaterial> _materials = new();
    public IReadOnlyList<PreviewMaterial> Materials => _materials;

    private ComPtr<ID3D11RasterizerState> _rasterCull;   // M354: per-material back-face culling
    private ComPtr<ID3D11ShaderResourceView> _white;
    private ComPtr<ID3D11ShaderResourceView> _whiteArray;
    private ComPtr<ID3D11ShaderResourceView> _whiteCube;
    /// <summary>M366: R32_FLOAT stand-in for shadow maps, which are sampled with sample_c.</summary>
    private ComPtr<ID3D11ShaderResourceView> _whiteDepth;
    private ComPtr<ID3D11ShaderResourceView> _whiteCubeArray;
    private ComPtr<ID3D11ShaderResourceView> _identityRamp;

    public string DeviceDescription { get; private set; } = "(no device)";
    public bool IsReady => _device.Handle is not null && _materials.Count > 0;

    /// <summary>M249: how many materials are live, so a host can tell "a scene is loaded" from "the
    /// fallback mesh is showing".</summary>
    public int MaterialCount => _materials.Count;
    public int DrawCalls { get; private set; }
    public double LastFrameMs { get; private set; }
    public PreviewMesh? Mesh { get; private set; }

    /// <summary>Every D3D call that mattered, newest last — the window shows this verbatim.</summary>
    public List<string> Diagnostics { get; } = new();

    private void Log(string s)
    {
        Diagnostics.Add(s);
        if (Diagnostics.Count > 400) Diagnostics.RemoveRange(0, 100);
    }

    // ---------------------------------------------------------------- device

    public bool Initialize(out string? error)
    {
        error = null;
        try
        {
            _d3d = SilkD3D11.GetApi(null);
            var levels = stackalloc D3DFeatureLevel[2] { D3DFeatureLevel.Level111, D3DFeatureLevel.Level110 };
            D3DFeatureLevel got = default;
            ComPtr<ID3D11Device> dev = default;
            ComPtr<ID3D11DeviceContext> ctx = default;

            int hr = _d3d.CreateDevice(default(ComPtr<IDXGIAdapter>), D3DDriverType.Hardware, 0,
                (uint)CreateDeviceFlag.None, levels, 2u, SilkD3D11.SdkVersion, ref dev, ref got, ref ctx);
            if (hr < 0)
            {
                error = $"D3D11CreateDevice failed: 0x{hr:X8}";
                Log(error);
                return false;
            }
            _device = dev;
            _ctx = ctx;
            DeviceDescription = $"D3D11 hardware device, feature level 0x{(uint)got:X}";
            Log(DeviceDescription);

            CreateStaticStates();
            return true;
        }
        catch (Exception ex)
        {
            error = $"D3D11 unavailable: {ex.Message}";
            Log(error);
            return false;
        }
    }

    private void CreateStaticStates()
    {
        // M294: ANISOTROPIC, now that there is a mip chain for it to work with.
        //
        // Trilinear alone picks one mip per pixel from the WORST-axis footprint, so a ground plane seen at
        // a grazing angle - which is most of a map from a normal camera - is forced to a blurry mip to
        // stop it aliasing along the other axis. Anisotropy samples the elongated footprint properly and
        // is what keeps distant ground readable rather than merely un-aliased. 8x is the usual quality
        // knee; the cost is trivial next to this renderer's per-frame readback.
        var sd = new SamplerDesc
        {
            Filter = Filter.Anisotropic, MaxAnisotropy = 8,
            AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
            MaxLOD = float.MaxValue, ComparisonFunc = ComparisonFunc.Never,
        };
        ComPtr<ID3D11SamplerState> s1 = default;
        _device.CreateSamplerState(in sd, ref s1);
        _linearWrap = s1;

        sd.AddressU = TextureAddressMode.Clamp;
        ComPtr<ID3D11SamplerState> s2 = default;
        _device.CreateSamplerState(in sd, ref s2);
        _linearClampU = s2;

        sd.AddressU = TextureAddressMode.Wrap;
        sd.AddressV = TextureAddressMode.Clamp;
        ComPtr<ID3D11SamplerState> s3 = default;
        _device.CreateSamplerState(in sd, ref s3);
        _linearClampV = s3;

        sd.AddressU = sd.AddressW = TextureAddressMode.Clamp;
        ComPtr<ID3D11SamplerState> s4 = default;
        _device.CreateSamplerState(in sd, ref s4);
        _linearClamp = s4;

        // M717: the mirror, for a slot that asks for it by its own authored address mode.
        sd.AddressU = sd.AddressV = sd.AddressW = TextureAddressMode.Mirror;
        ComPtr<ID3D11SamplerState> sMirror = default;
        _device.CreateSamplerState(in sd, ref sMirror);
        _linearMirror = sMirror;

        // M254: a COMPARISON sampler, for the shadow lookups.
        //
        // sample_c evaluates through the sampler's ComparisonFunc, and the two states above use
        // ComparisonFunc.Never - which means exactly what it says: every tap fails. League's foliage
        // shader lights itself from five PCF taps and then does
        //     mad r0.xyz, shadowTerm, SHADOW_COLOR_COMPLEMENT, SHADOW_COLOR
        // so a term pinned at 0 collapses the result to SHADOW_COLOR, the fully-shadowed colour, and every
        // shadow-sampling surface renders black. That is what the M252 A/B diff showed as black foliage
        // silhouettes across the whole map.
        //
        // Always rather than LessEqual, because there is no shadow map: the stand-in is an opaque white
        // 1x1 and the preview renders no shadow pass. Always means "nothing occludes anything", which is
        // the honest answer when no depth has been rendered - LessEqual against a stand-in would be
        // comparing against a number that means nothing.
        var cmp = new SamplerDesc
        {
            Filter = Filter.ComparisonMinMagMipLinear,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue, ComparisonFunc = ComparisonFunc.Always,
        };
        ComPtr<ID3D11SamplerState> s5 = default;
        _device.CreateSamplerState(in cmp, ref s5);
        _comparison = s5;

        // M465: and the one that means something, for the frames a real shadow map exists.
        //
        // The Always state above is correct ONLY while the stand-in is bound - it is what keeps every tap
        // returning "lit" against a texture whose contents mean nothing. Left in place once a real depth map
        // arrives it would defeat the whole pass in complete silence: the map would be rendered, bound and
        // sampled, every comparison would pass, and the frame would be identical to one with no shadows.
        //
        // LessEqual because the depth stored is the CLOSEST caster to the sun and the reference is the
        // receiver's own depth: a receiver behind a caster has the larger depth, the comparison fails, and
        // the tap returns 0 = shadowed. Equal is included so a surface comparing against ITSELF - which is
        // every lit surface that also cast - counts as lit; that is the case the bias exists to protect,
        // and Less alone would make it a coin toss on the first texel of every sunlit slope.
        //
        // Filter stays ComparisonMinMagMipLinear: on a comparison sampler that is hardware PCF, so each of
        // Riot's five taps is itself a bilinear blend of four comparisons. Twenty effective samples for the
        // price of five is why the kernel is as small as it is.
        var cmpLe = cmp;
        cmpLe.ComparisonFunc = ComparisonFunc.LessEqual;
        ComPtr<ID3D11SamplerState> s6 = default;
        _device.CreateSamplerState(in cmpLe, ref s6);
        _comparisonLessEqual = s6;

        // Opaque white stand-ins must match the reflected resource DIMENSION. D3D11 accepts a Texture2D
        // SRV at a Texture2DArray/TextureCubeArray slot, but sampling the mismatched view returns zero.
        // PBR shaders then multiply their otherwise valid albedo by a black IBL/terrain sample.
        var px = new byte[] { 255, 255, 255, 255 };
        _white = MakeTexture(px, 1, 1) ?? default;
        _whiteArray = MakeTexture(px, 1, 1, resourceDimension: 5) ?? default;
        _whiteCube = MakeTexture(px, 1, 1, resourceDimension: 9) ?? default;
        _whiteCubeArray = MakeTexture(px, 1, 1, resourceDimension: 10) ?? default;

        // M366: a DEPTH stand-in, for shadow maps specifically. Latent undefined behaviour, measured not
        // assumed: CheckFormatSupport on R8G8B8A8_UNORM reports SHADER_SAMPLE = true but
        // SHADER_SAMPLE_COMPARISON = FALSE, and every NO_BAKED_LIGHTING blob reads
        // SHADOW_MAP_DEPTH_PCF_SharedTexture with sample_c. Handing it _white is therefore a comparison
        // sample against a format that does not support one - it happens to return 1 ("unshadowed") on the
        // GPU this was measured on, but a driver returning 0 collapses the light term to SHADOW_COLOR,
        // which is a 2.86x darkening of exactly the surfaces this renderer draws most.
        //
        // It is NOT safe to assume ComparisonFunc.Always rescues it: with Always set, a BLACK texel
        // measured 0.0 rather than 1.0, so the comparison function is genuinely ignored on that format and
        // the M254 note nearby is wrong about why this works. R32_FLOAT does support comparison sampling,
        // so with it the sampler means what it says.
        _whiteDepth = MakeTexture(BitConverter.GetBytes(1f), 1, 1, format: Format.FormatR32Float) ?? default;

        // M221: the colour-remap ramp stand-in must be TRANSPARENT, and the shader says so itself.
        //
        // Disassembling skinnedmesh/diffuse_alpha ps blob 5 settles what two rounds of guessing could not:
        //
        //     dp3  r1.x, r0.yzwy, l(0.2126, 0.7152, 0.0722)   // luma, Rec.709
        //     mov  r1.y, l(0.5)
        //     sample r1.xyzw, r1.xyxx, t1.xyzw, s15           // ramp.Sample(luma, 0.5)
        //     lt   r1.w, l(0.000000), r1.w                    // is the sampled ALPHA > 0 ?
        //     movc r0.yzw, r1.wwww, r1.xxyz, r0.yyzw          // yes -> replace rgb; no -> keep it
        //
        // The remap is GATED ON THE RAMP'S ALPHA. It is not unconditional, which is what the D3D11 spike
        // assumed and what M218/M219 inherited. Any opaque ramp forces the replacement - white gave a white
        // model, a greyscale identity gave a black-and-white one - because alpha was 255 in both.
        //
        // Alpha zero and the shader skips the stage on its own, keeping the lit diffuse. That is a real
        // no-op rather than an approximation of one, and it is almost certainly what the engine binds when
        // no colour grading is active. The rgb is irrelevant; only the alpha is read.
        _identityRamp = MakeTexture(new byte[] { 0, 0, 0, 0 }, 1, 1) ?? default;
    }

    // ---------------------------------------------------------------- shaders

    /// <summary>Bench mode: replace the scene with a single material covering the whole mesh.</summary>
    public ShaderLoadReport LoadShaders(DxbcShader vsRefl, DxbcShader psRefl)
    {
        ClearMaterials();
        var m = BuildMaterial("(single shader)", vsRefl, psRefl, 0, -1, out var r);
        if (m is not null) _materials.Add(m);
        return r;
    }

    /// <summary>M232: the additive blend state, selected per material by <see cref="PreviewMaterial.Additive"/>.</summary>
    private ComPtr<ID3D11BlendState> _blendAdditive;
    /// <summary>M720: particle blend states, built once per distinct state and kept for the device's life.</summary>
    private readonly Dictionary<(bool Enabled, ReyEngine.Formats.Vfx.VfxBlendFactor Src, ReyEngine.Formats.Vfx.VfxBlendFactor Dst,
        ReyEngine.Formats.Vfx.VfxBlendOp Op, bool WritesColor), ComPtr<ID3D11BlendState>> _particleBlendStates = new();
    private readonly Dictionary<(MaterialBlendFactor Src, MaterialBlendFactor Dst), ComPtr<ID3D11BlendState>>
        _authoredBlendStates = new();

    public void ClearMaterials()
    {
        foreach (var m in _materials) m.Dispose();
        _materials.Clear();
        foreach (var b in _sharedCbs.Values) b.Dispose();
        _sharedCbs.Clear();
        // M226: the pool outlives individual materials but not the scene
        foreach (var t in _texPool.Values) t.Dispose();
        foreach (var t in _retired) t.Dispose();
        _texPool.Clear();
        _texAlpha.Clear();
        _retired.Clear();
        // M283: mesh geometry is per-emitter and owned here, so it dies with the scene that referenced it.
        // The handles materials hold are indices into this list, which is why it is cleared alongside them
        // rather than lazily - a stale handle would index another emitter's mesh.
        ReleaseMeshGeometry();
        _comparePs.Dispose();
        _comparePs = default;
    }

    /// <summary>M242: the immutable half of a pipeline - the two shader objects and the input layout.
    /// Everything else about a material (constant buffer contents, textures, draw range) differs per
    /// material and is not shared.</summary>
    private sealed class CachedPipeline
    {
        public ComPtr<ID3D11VertexShader> Vs;
        public ComPtr<ID3D11PixelShader> Ps;
        public ComPtr<ID3D11InputLayout> Layout;
        public int Id;

        public void Dispose() { Vs.Dispose(); Ps.Dispose(); Layout.Dispose(); }
    }

    private readonly Dictionary<PipelineKey, CachedPipeline> _pipelines = new();

    /// <summary>Pipelines currently held, and how many builds the cache satisfied without touching the
    /// driver. Surfaced so a scene report can show the ratio rather than claim an improvement.</summary>
    public int CachedPipelineCount => _pipelines.Count;
    public int PipelineCacheHits { get; private set; }
    public int PipelineCacheMisses { get; private set; }

    /// <summary>The game build the cache is keyed against. Set by the host; changing it does not by itself
    /// invalidate anything, because the bytecode hash already covers correctness - this is what makes the
    /// cache PRUNABLE on patch day and what a user-facing message keys on.</summary>
    public string GameVersion { get; set; } = "unknown";

    /// <summary>Drop every cached pipeline. The scene's materials must be gone first - they hold
    /// non-owning references to exactly these objects.</summary>
    public void ClearPipelineCache()
    {
        foreach (var pl in _pipelines.Values) pl.Dispose();
        _pipelines.Clear();
        PipelineCacheHits = PipelineCacheMisses = 0;
    }

    /// <summary>M214: bring up one material's pipeline. <paramref name="indexCount"/> below zero means the
    /// whole index buffer, which is what the bench uses.
    ///
    /// <para>M242: the shader objects and input layout come from the pipeline cache when an identical
    /// (shader, permutation, state, backend) combination has already been built. Map12 was building 921 of
    /// these for 120 material names, and CreateVertexShader / CreatePixelShader / CreateInputLayout are the
    /// expensive calls in here - they are where the driver compiles.</para></summary>
    public PreviewMaterial? BuildMaterial(string name, DxbcShader vsRefl, DxbcShader psRefl,
        int startIndex, int indexCount, out ShaderLoadReport r,
        ShaderDescription? vsDesc = null, ShaderDescription? psDesc = null,
        StateDescription? state = null)
    {
        r = new ShaderLoadReport();
        if (_device.Handle is null) { r.Error = "no D3D11 device"; return null; }

        // Only cacheable when the caller supplied the descriptions that identify the variant. Without them
        // there is no honest key - two materials could share a shader NAME and differ in permutation - so
        // the uncached path stays, rather than inventing a key that might collide.
        PipelineKey? key = vsDesc is not null && psDesc is not null
            ? PipelineKey.For(vsDesc, psDesc, state ?? StateDescription.Geometry, GameVersion, RenderBackend.D3D11)
            : null;

        if (key is { } k && _pipelines.TryGetValue(k, out var hit))
        {
            PipelineCacheHits++;
            var shared = new PreviewMaterial
            {
                Name = name, VsRefl = vsRefl, PsRefl = psRefl,
                StartIndex = startIndex, IndexCount = indexCount,
                OwnsPipeline = false,
                Vs = hit.Vs, Ps = hit.Ps, Layout = hit.Layout,
                PipelineId = hit.Id,
            };
            // Constant buffers are per material even on a hit - same layout, different contents.
            CreateConstantBuffers(vsRefl, shared.VsCbs, r, "vertex");
            CreateConstantBuffers(psRefl, shared.PsCbs, r, "pixel");
            r.Step($"pipeline cache HIT ({_pipelines.Count} resident)");
            r.Success = true;
            return shared;
        }
        if (key is not null) PipelineCacheMisses++;

        var mat = new PreviewMaterial
        {
            Name = name, VsRefl = vsRefl, PsRefl = psRefl,
            StartIndex = startIndex, IndexCount = indexCount,
        };

        // ---- 1. the shader objects themselves
        ComPtr<ID3D11VertexShader> vs = default;
        int hr;
        fixed (byte* p = vsRefl.Bytecode)
            hr = _device.CreateVertexShader(p, (nuint)vsRefl.Bytecode.Length,
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vs);
        if (hr < 0)
        {
            r.Error = $"CreateVertexShader failed: 0x{hr:X8}"
                      + (vsRefl.WasTrimmed ? "" : " (bytecode was NOT trimmed to its declared size - see ShaderCacheReader)");
            Log(r.Error);
            mat.Dispose();
            return null;
        }
        mat.Vs = vs;
        r.Step($"CreateVertexShader OK ({vsRefl.ByteSize:n0} bytes, {vsRefl.ShaderModel})");

        ComPtr<ID3D11PixelShader> ps = default;
        fixed (byte* p = psRefl.Bytecode)
            hr = _device.CreatePixelShader(p, (nuint)psRefl.Bytecode.Length,
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref ps);
        if (hr < 0)
        {
            r.Error = $"CreatePixelShader failed: 0x{hr:X8}"
                      + (psRefl.WasTrimmed ? "" : " (bytecode was NOT trimmed to its declared size)");
            Log(r.Error);
            mat.Dispose();
            return null;
        }
        mat.Ps = ps;
        r.Step($"CreatePixelShader OK ({psRefl.ByteSize:n0} bytes, {psRefl.ShaderModel})");

        // ---- 2. input layout, generated from the vertex shader's own signature
        if (!CreateInputLayout(mat, r)) { mat.Dispose(); return null; }

        // ---- 3. one constant buffer per reflected cbuffer, sized as the shader declares
        CreateConstantBuffers(vsRefl, mat.VsCbs, r, "vs");
        CreateConstantBuffers(psRefl, mat.PsCbs, r, "ps");

        // M242: publish the immutable half so the next material with the same key skips the driver calls
        // above. The material keeps using its own handles - the cache holds the SAME COM objects, and the
        // ownership flag is what stops the material from releasing them out from under the cache.
        if (key is { } store)
        {
            _pipelines[store] = new CachedPipeline { Vs = mat.Vs, Ps = mat.Ps, Layout = mat.Layout, Id = _pipelines.Count };
            mat.PipelineId = _pipelines.Count - 1;
            mat.OwnsPipeline = false;
            r.Step($"pipeline cached ({_pipelines.Count} resident)");
        }

        r.Success = true;
        return mat;
    }

    public void AddMaterial(PreviewMaterial m) => _materials.Add(m);

    /// <summary>M266: drop only the materials matching <paramref name="pred"/>, and return how many went.
    ///
    /// <para>Deliberately does NOT touch _texPool, _retired or _sharedCbs the way ClearMaterials does: the
    /// map scene is still using all three, and rebuilding it costs seconds. Retracting a particle playback
    /// has to be possible without wiping the ~1,600 materials sitting underneath it.</para>
    ///
    /// <para>Disposing here is safe because cache-hit materials carry OwnsPipeline=false - Dispose releases
    /// their own constant buffers and leaves the shared shader objects and input layout alone.</para></summary>
    public int RemoveMaterials(Predicate<PreviewMaterial> pred)
    {
        int n = 0;
        for (int i = _materials.Count - 1; i >= 0; i--)
            if (pred(_materials[i])) { _materials[i].Dispose(); _materials.RemoveAt(i); n++; }
        return n;
    }

    private bool CreateInputLayout(PreviewMaterial mat, ShaderLoadReport r)
    {
        var vsRefl = mat.VsRefl;
        // D3D validates the layout against the WHOLE input signature, so every non-system element needs an
        // entry even when the mesh has no data for it. Unknown semantics alias the zero pad.
        var elems = new List<InputElementDesc>();
        var names = new List<GCHandle>();
        try
        {
            foreach (var e in vsRefl.Inputs)
            {
                if (e.SystemValueType != 0) continue;              // SV_VertexID and friends come from the system

                var nameBytes = System.Text.Encoding.ASCII.GetBytes(e.Semantic + "\0");
                var h = GCHandle.Alloc(nameBytes, GCHandleType.Pinned);
                names.Add(h);

                (uint offset, int comps, bool known) = MapSemantic(e.Semantic, (int)e.Index, e.ComponentCount);
                if (!known) r.UnmatchedInputs.Add(e.FullSemantic);

                elems.Add(new InputElementDesc
                {
                    SemanticName = (byte*)h.AddrOfPinnedObject(),
                    SemanticIndex = e.Index,
                    Format = FormatFor(e.ComponentType, comps),
                    InputSlot = 0,
                    AlignedByteOffset = offset,
                    InputSlotClass = InputClassification.PerVertexData,
                    InstanceDataStepRate = 0,
                });
            }

            if (elems.Count == 0)
            {
                r.Error = "the vertex shader declares no input elements - nothing to build a layout from";
                return false;
            }

            ComPtr<ID3D11InputLayout> layout = default;
            int hr;
            fixed (InputElementDesc* pe = elems.ToArray())
            fixed (byte* pb = vsRefl.Bytecode)
                hr = _device.CreateInputLayout(pe, (uint)elems.Count, pb, (nuint)vsRefl.Bytecode.Length, ref layout);

            if (hr < 0)
            {
                r.Error = $"CreateInputLayout failed: 0x{hr:X8} over {elems.Count} elements "
                          + $"({string.Join(", ", vsRefl.Inputs.Select(i => i.FullSemantic))})";
                Log(r.Error);
                return false;
            }
            mat.Layout = layout;
            r.Step($"CreateInputLayout OK ({elems.Count} elements)");
            if (r.UnmatchedInputs.Count > 0)
                r.Warn($"the test mesh has no data for {string.Join(", ", r.UnmatchedInputs)} - those read as zero");
            return true;
        }
        finally
        {
            foreach (var h in names) h.Free();
        }
    }

    /// <summary>M231: does this material's vertex shader want mProj to be the whole world-to-clip transform?
    /// True when it uses mProj without a bone palette, which across the cache selects exactly the particle
    /// shaders. Null material (the built-in comparison path) keeps the champion reading.</summary>
    /// <summary>M646: whether this draw's <c>mProj</c> must be the full view-projection. Particle-style
    /// shaders always (M231); bone shaders whenever their palette is posed in world space, which is the
    /// default - under the old view-space pose the bones carried the view and mProj was projection alone.</summary>
    private static bool NeedsViewProjection(PreviewMaterial? mat, PreviewSettings s)
    {
        if (ParticleStyleProjection(mat)) return true;
        if (mat is null || s.BonePose is BonePose.View or BonePose.ViewTransposed) return false;
        return mat.VsRefl.ConstantBuffers.Any(cb => cb.Name.Contains("Bone", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ParticleStyleProjection(PreviewMaterial? mat)
    {
        if (mat is null) return false;

        // A bone palette means the champion reading: bones already applied object-to-view.
        if (mat.VsRefl.ConstantBuffers.Any(cb => cb.Name.Contains("Bone", StringComparison.OrdinalIgnoreCase)))
            return false;

        // env_scrollingdiffuse and the four tft_* shaders use mProj AND a view-projection matrix. Whatever
        // their mProj is for, it is not the world-to-clip transform - that job is already taken - so they are
        // explicitly excluded rather than swept in by "has no bones".
        if (mat.VsRefl.ConstantBuffers.Any(cb => cb.Variables.Any(v => v.IsUsed
                && v.Name.Contains("VIEW_PROJECTION", StringComparison.OrdinalIgnoreCase))))
            return false;

        return true;
    }

    /// <summary>Tiles a float4 <paramref name="count"/> times - for cbuffer array constants.</summary>
    private static float[] Repeat(float[] v, int count)
    {
        var r = new float[v.Length * count];
        for (int i = 0; i < count; i++) Array.Copy(v, 0, r, i * v.Length, v.Length);
        return r;
    }

    /// <summary>Semantic → byte offset inside <see cref="PreviewVertex"/>.</summary>
    private static (uint Offset, int Components, bool Known) MapSemantic(string semantic, int index, int declared)
        => (semantic.ToUpperInvariant(), index) switch
        {
            ("POSITION", 0) => (0u, 3, true),
            ("NORMAL", 0) => (12u, 3, true),
            ("TANGENT", 0) => (24u, 4, true),
            // M232: four components. quad_vs packs frame index and erosion drive into .zw - see PreviewVertex.
            ("TEXCOORD", 0) => (40u, 4, true),
            ("TEXCOORD", 1) => (56u, 2, true),
            ("TEXCOORD", 2) => (64u, 2, true),
            ("TEXCOORD", 3) => (72u, 2, true),
            ("TEXCOORD", 5) => (136u, 3, true),                  // M230: the grass clump pivot
            ("TEXCOORD", 7) => (80u, 2, true),                   // M224: the lightmap UV
            ("COLOR", 0) => (88u, 4, true),
            ("BLENDWEIGHT", 0) => (104u, 4, true),
            ("BLENDINDICES", 0) => (120u, 4, true),
            _ => (152u, Math.Max(1, declared), false),           // the zero pad
        };

    private static Format FormatFor(uint componentType, int comps) => componentType switch
    {
        1 => comps switch { 1 => Format.FormatR32Uint, 2 => Format.FormatR32G32Uint, 3 => Format.FormatR32G32B32Uint, _ => Format.FormatR32G32B32A32Uint },
        2 => comps switch { 1 => Format.FormatR32Sint, 2 => Format.FormatR32G32Sint, 3 => Format.FormatR32G32B32Sint, _ => Format.FormatR32G32B32A32Sint },
        _ => comps switch { 1 => Format.FormatR32Float, 2 => Format.FormatR32G32Float, 3 => Format.FormatR32G32B32Float, _ => Format.FormatR32G32B32A32Float },
    };

    private void CreateConstantBuffers(DxbcShader refl, Dictionary<int, ComPtr<ID3D11Buffer>> into,
        ShaderLoadReport r, string stage)
    {
        foreach (var cb in refl.ConstantBuffers)
        {
            if (cb.BindPoint < 0) { r.Warn($"{stage}: cbuffer '{cb.Name}' has no bind point and is skipped"); continue; }
            var desc = new BufferDesc
            {
                ByteWidth = (uint)Math.Max(16, cb.AllocationSize),
                Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.ConstantBuffer,
                CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> buf = default;
            int hr = _device.CreateBuffer(in desc, null, ref buf);
            if (hr < 0) { r.Warn($"{stage}: CreateBuffer for '{cb.Name}' failed 0x{hr:X8}"); continue; }
            into[cb.BindPoint] = buf;
            r.Step($"{stage} cbuffer b{cb.BindPoint} '{cb.Name}' ({cb.Size} bytes, {cb.Variables.Count} vars)");
        }
    }

    /// <summary>M210: ReyEngine's material model as a pixel shader, so the two can be compared directly.
    ///
    /// <para>The HLSL is GENERATED from the Riot vertex shader's own output signature rather than written by
    /// hand. D3D requires a pixel shader's input signature to be a matching subset of the bound vertex
    /// shader's outputs, and every League shader outputs a different interpolant set - so one fixed hand
    /// written comparison shader would link against a few shaders and fail on the rest. Mirroring the
    /// signature makes it link against whatever happens to be loaded.</para>
    ///
    /// <para>The shading is deliberately only what ReyEngine's material path does: diffuse x (lambert sun +
    /// ambient). It is precisely the approximation whose fidelity is in question, so it must not quietly
    /// acquire any of Riot's extra stages.</para></summary>
    public bool BuildComparisonShader(DxbcShader vsRefl, out string? error)
    {
        // one comparison shader, generated from the FIRST material's vertex signature
        error = null;
        _comparePs.Dispose();
        _comparePs = default;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Texture2D gDiffuse : register(t0);");
        sb.AppendLine("SamplerState gSamp : register(s0);");
        sb.AppendLine("cbuffer CompareCB : register(b0) { float4 gSunDir; float4 gSunColor; };");
        sb.AppendLine("struct PSIn {");

        // M671: the uv and normal come from the same picker the debug views use - see
        // PickDebugInterpolators for the signature that broke the inline rule this replaced.
        var pick = PickDebugInterpolators(vsRefl);
        string? uvField = pick.Uv, normalField = pick.Normal;
        int n = 0;
        foreach (var o in vsRefl.Outputs)
        {
            int comps = Math.Max(1, o.ComponentCount);
            string type = comps == 1 ? "float" : "float" + comps;
            string field = "f" + n++;
            bool isPos = o.SystemValueType == 1
                         || o.Semantic.StartsWith("SV_", StringComparison.OrdinalIgnoreCase);
            string sem = isPos ? "SV_Position" : o.FullSemantic;
            sb.AppendLine("    " + type + " " + field + " : " + sem + ";");
        }
        sb.AppendLine("};");
        sb.AppendLine("float4 main(PSIn i) : SV_Target {");
        sb.AppendLine(uvField is null ? "    float2 uv = float2(0.5, 0.5);" : "    float2 uv = i." + uvField + ".xy;");
        sb.AppendLine("    float4 d = gDiffuse.Sample(gSamp, uv);");
        sb.AppendLine(normalField is null
            ? "    float ndl = 0.75;"
            : "    float ndl = saturate(dot(normalize(i." + normalField + ".xyz), -gSunDir.xyz));");
        sb.AppendLine("    float3 lit = d.rgb * (gSunColor.rgb * ndl + 0.25);");
        sb.AppendLine("    return float4(lit, d.a);");
        sb.AppendLine("}");

        ComparisonShaderSource = sb.ToString();
        var src = System.Text.Encoding.ASCII.GetBytes(ComparisonShaderSource);
        var entry = System.Text.Encoding.ASCII.GetBytes("main\0");
        var target = System.Text.Encoding.ASCII.GetBytes("ps_5_0\0");
        ID3D10Blob* code = null, errs = null;
        int hr;
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* sp = src)
            fixed (byte* ep = entry)
            fixed (byte* tp = target)
                hr = compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                    ep, tp, 0u, 0u, &code, &errs);
        }
        catch (Exception ex) { error = "the HLSL compiler is unavailable: " + ex.Message; return false; }

        if (hr < 0 || code is null)
        {
            error = errs is not null
                ? "comparison shader failed to compile: " + SilkMarshal.PtrToString((nint)errs->GetBufferPointer())
                : string.Format("comparison shader failed to compile: 0x{0:X8}", hr);
            Log(error);
            return false;
        }

        ComPtr<ID3D11PixelShader> cps = default;
        hr = _device.CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(),
            ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref cps);
        if (hr < 0)
        {
            error = string.Format("CreatePixelShader (comparison) failed 0x{0:X8}", hr);
            Log(error);
            return false;
        }
        _comparePs = cps;
        Log("comparison shader built from the vertex shader's output signature");
        return true;
    }

    /// <summary>How many highlight ranges were drawn in the last frame - 0 when nothing is selected, and
    /// also 0 if the overlay pipeline could not be built, which is worth telling apart.</summary>
    public int HighlightDraws { get; private set; }

    public bool HasComparisonShader => _comparePs.Handle is not null;

    /// <summary>Ranges of the STATIC index buffer to draw as a selection highlight, in the same units
    /// mapgeo groups use.</summary>
    public int HighlightRangeCount => _highlight.Count;

    /// <summary>Colour of the highlight overlay. Alpha is the blend weight over the shaded pixel.</summary>
    /// <summary>M381: GL's outline colour (the Kalista accent ViewportMeshRenderer draws with), opaque.
    /// Was a translucent orange fill; the two viewports now mark selection identically.</summary>
    public Vector4 HighlightColor = new(0.21f, 0.89f, 0.76f, 1f);

    /// <summary>
    /// <para>Whether the highlight is occluded by geometry in front of it. Defaults OFF, against the
    /// instinct, because the instinct was measured and lost: with the test on, the overlay changed 0.03%
    /// of pixels; with it off, 1.29% - on the same on-screen geometry. The overlay re-derives clip depth
    /// in its own vertex shader, so it does not land bit-identically on what Riot's shader wrote for the
    /// same triangle, and at equal depth LessEqual is a coin toss the highlight loses.</para>
    ///
    /// <para>The cost is that a selection behind a wall still shows. For a selection marker that is
    /// arguably right - you want to see what you picked - and it beats the alternative, which is a
    /// highlight that silently does nothing. The `highlight` harness mode measures both.</para>
    /// </summary>
    public bool HighlightDepthTest = false;

    /// <summary>
    /// <para>M269: mark index RANGES for the selection highlight - not materials.</para>
    ///
    /// <para>The obvious API would take material indices, and it would be wrong.
    /// <c>Dx11SceneBuilder.MergeSlices</c> sorts the map's groups by start index and merges adjacent ones,
    /// so a material does not correspond to a submesh and the Nth material is not the Nth group. Marking
    /// materials would highlight confidently, and highlight the wrong geometry. A range is what mapgeo
    /// actually stores and survives the merge untouched.</para>
    /// </summary>
    /// <summary>
    /// <para>M270: the placement markers the GL viewport draws - one camera-facing quad per placement,
    /// coloured by type. Replaces the whole set; pass nothing to clear.</para>
    ///
    /// <para>Grouped by colour on submission so the draw is one call per TYPE rather than per placement:
    /// a Summoner's Rift bin carries a thousand of these and per-marker draws would cost more than the map
    /// behind them.</para>
    /// </summary>
    public void SetIcons(IReadOnlyList<(Vector3 Pos, Vector4 Color, float Size, ViewportIcon Glyph)>? icons)
    {
        if (ReferenceEquals(_iconSource, icons)) return;
        _iconSource = icons;
        _icons.Clear();
        if (icons is null) return;
        foreach (var i in icons) if (i.Size > 0f) _icons.Add(i);
        // Ordered by GLYPH first and colour second, so a batch is one texture bind and one cbuffer write.
        // The sort is on values, not references, so the batching is deterministic frame to frame.
        _icons.Sort((a, b) =>
        {
            int g = ((int)a.Glyph).CompareTo((int)b.Glyph);
            return g != 0 ? g : Key(a.Color).CompareTo(Key(b.Color));
        });
        static long Key(Vector4 c) =>
            ((long)(c.X * 255) << 24) | ((long)(c.Y * 255) << 16) | ((long)(c.Z * 255) << 8) | (long)(c.W * 255);
    }

    /// <summary>How many marker draws the last frame issued - one per distinct colour, not per marker.</summary>
    public int IconDraws { get; private set; }

    public int IconCount => _icons.Count;

    private int DrawIcons(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_icons.Count == 0) return 0;
        if (!EnsureOverlay()) return 0;

        // Camera basis from the MIRROR-INCLUSIVE view's inverse, the same derivation the particles use -
        // an origin-relative approximation is only correct for a marker at the world origin, and these are
        // scattered across the whole map.
        Matrix4x4.Invert(view, out var inv);
        var right = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, inv));
        var up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, inv));

        int quads = _icons.Count;
        EnsureIconBuffers(quads * 4, quads * 6);
        if (_iconVb.Handle is null || _iconIb.Handle is null) return 0;

        bool countChanged = _iconVertsCpu.Length != quads * 4;
        if (countChanged)
        {
            _iconVertsCpu = new PreviewVertex[quads * 4];
            _iconIndicesCpu = new uint[quads * 6];
        }
        var verts = _iconVertsCpu;
        var idx = _iconIndicesCpu;
        var mvp = Matrix4x4.Multiply(view, proj);
        for (int i = 0; i < quads; i++)
        {
            var (pos, _, size, _) = _icons[i];
            // M657: a marker is a world-sized billboard, so flying in to move something made its icon
            // cover the thing being placed. Capped here rather than in a shader because these quads are
            // built on the CPU; the GL vertex shader does the same arithmetic per vertex.
            size = ViewportIcons.CapWorldSize(pos, up, size, mvp, _height);
            float h = size * 0.5f;
            var r = right * h; var u = up * h;
            int v = i * 4;
            verts[v + 0].Position = pos - r + u; verts[v + 0].Uv0 = new Vector4(0f, 0f, 0f, 0f);
            verts[v + 1].Position = pos + r + u; verts[v + 1].Uv0 = new Vector4(1f, 0f, 0f, 0f);
            verts[v + 2].Position = pos + r - u; verts[v + 2].Uv0 = new Vector4(1f, 1f, 0f, 0f);
            verts[v + 3].Position = pos - r - u; verts[v + 3].Uv0 = new Vector4(0f, 1f, 0f, 0f);
            if (countChanged)
            {
                int o = i * 6;
                idx[o + 0] = (uint)v; idx[o + 1] = (uint)(v + 1); idx[o + 2] = (uint)(v + 2);
                idx[o + 3] = (uint)v; idx[o + 4] = (uint)(v + 2); idx[o + 5] = (uint)(v + 3);
            }
        }
        UploadIcons(verts, idx);

        uint stride = PreviewVertex.SizeInBytes, offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _iconVb, in stride, in offset);
        _ctx.IASetIndexBuffer(_iconIb, Format.FormatR32Uint, 0);
        // Textured when the glyph pipeline came up, flat squares when it did not - a marker in the wrong
        // shape still tells you something is there, which beats no marker.
        bool textured = _overlayVsTex.Handle is not null && _overlayLayoutTex.Handle is not null;
        _ctx.IASetInputLayout(textured ? _overlayLayoutTex : _overlayLayout);
        _ctx.VSSetShader(textured ? _overlayVsTex : _overlayVs, null, 0);
        _ctx.PSSetShader(textured ? _overlayPsTex : _overlayPs, null, 0);
        if (textured) _ctx.PSSetSamplers(0, 1, ref _iconSampler);
        // M659: depth-TESTED by default (write off, so icons never occlude each other), which is what
        // makes a marker say where something is rather than only that it exists. IconsThroughWalls is the
        // old always-on-top behaviour, kept as a toggle.
        _ctx.OMSetDepthStencilState(IconsThroughWalls ? _overlayDepthNoTest : _overlayDepth, 0);
        var factor = stackalloc float[4] { 0, 0, 0, 0 };
        _ctx.OMSetBlendState(_overlayBlend, factor, 0xFFFFFFFF);

        int draws = 0, runStart = 0;
        for (int i = 1; i <= quads; i++)
        {
            if (i < quads && _icons[i].Color == _icons[runStart].Color
                          && _icons[i].Glyph == _icons[runStart].Glyph) continue;
            SetOverlayCb(mvp, _icons[runStart].Color);
            _ctx.VSSetConstantBuffers(0, 1, ref _overlayCb);
            _ctx.PSSetConstantBuffers(0, 1, ref _overlayCb);
            if (textured)
            {
                int gi = (int)_icons[runStart].Glyph;
                if (gi >= 0 && gi < _glyphSrv.Length && _glyphSrv[gi].Handle is not null)
                    _ctx.PSSetShaderResources(0, 1, ref _glyphSrv[gi]);
            }
            _ctx.DrawIndexed((uint)((i - runStart) * 6), (uint)(runStart * 6), 0);
            draws++;
            runStart = i;
        }
        return draws;
    }

    private void EnsureIconBuffers(int verts, int indices)
    {
        if (_iconVb.Handle is not null && verts <= _iconVbCapacity && indices <= _iconIbCapacity) return;
        _iconVb.Dispose(); _iconIb.Dispose();
        _iconVb = default; _iconIb = default;
        _iconVbCapacity = Math.Max(verts, 64);
        _iconIbCapacity = Math.Max(indices, 96);

        var vd = new BufferDesc
        {
            ByteWidth = (uint)(_iconVbCapacity * PreviewVertex.SizeInBytes), Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.VertexBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> vb = default;
        _device.CreateBuffer(in vd, null, ref vb);
        _iconVb = vb;

        var id = new BufferDesc
        {
            ByteWidth = (uint)(_iconIbCapacity * sizeof(uint)), Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.IndexBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> ib = default;
        _device.CreateBuffer(in id, null, ref ib);
        _iconIb = ib;
    }

    private void UploadIcons(PreviewVertex[] verts, uint[] indices)
    {
        MappedSubresource mv = default;
        if (_ctx.Map(_iconVb, 0, Map.WriteDiscard, 0, ref mv) >= 0)
        {
            fixed (PreviewVertex* src = verts)
                System.Buffer.MemoryCopy(src, mv.PData,
                    (long)_iconVbCapacity * PreviewVertex.SizeInBytes,
                    (long)verts.Length * PreviewVertex.SizeInBytes);
            _ctx.Unmap(_iconVb, 0);
        }
        MappedSubresource mi = default;
        if (_ctx.Map(_iconIb, 0, Map.WriteDiscard, 0, ref mi) >= 0)
        {
            fixed (uint* src = indices)
                System.Buffer.MemoryCopy(src, mi.PData, (long)_iconIbCapacity * sizeof(uint),
                    (long)indices.Length * sizeof(uint));
            _ctx.Unmap(_iconIb, 0);
        }
    }

    private void SetOverlayCb(Matrix4x4 mvp, Vector4 color)
    {
        var bytes = new byte[80];
        var m = new[]
        {
            mvp.M11, mvp.M12, mvp.M13, mvp.M14, mvp.M21, mvp.M22, mvp.M23, mvp.M24,
            mvp.M31, mvp.M32, mvp.M33, mvp.M34, mvp.M41, mvp.M42, mvp.M43, mvp.M44,
            color.X, color.Y, color.Z, color.W,
        };
        System.Buffer.BlockCopy(m, 0, bytes, 0, 80);
        Upload(_overlayCb, bytes, 80);
    }

    /// <summary>
    /// M386: replace a shared texture on every committed material that binds it, in place.
    ///
    /// <para>For state-driven textures — the map's grass tint is the case this exists for — where the
    /// scene is otherwise unchanged. Re-preparing the scene to swap one SRV rebuilds every material,
    /// which is why the D3D11 viewport used to keep whichever tint it was built with.</para>
    ///
    /// <para>Only materials that ALREADY bind one of <paramref name="targets"/> are touched; this never
    /// introduces a binding that the permutation did not reflect. Returns the number of slot bindings
    /// replaced — zero means no committed material has such a slot, not a failure.</para>
    /// </summary>
    /// <summary>
    /// M395: bind ONE target to ONE texture, across every committed material that already binds it.
    ///
    /// <para>The list-taking overload below applies a single texture to N targets, which is right for a
    /// settled state and useless for a transition: the crossfade needs GRASS_TINT_MAP and
    /// GRASS_TINT_MAP_ALTERNATE holding DIFFERENT textures at once. The texture pool is keyed
    /// Ordinal, so two paths are already two entries - nothing else had to change.</para>
    ///
    /// <para>As with the list overload, a target the permutation did not reflect is never introduced,
    /// only replaced.</para>
    /// </summary>
    public int RebindSharedTexture(string target, string poolKey, byte[] rgba, int width, int height)
    {
        int rebound = 0;
        foreach (var mat in Materials)
        {
            if (!mat.Textures.ContainsKey(target)) continue;
            if (!TryBindCached(mat, target, poolKey)) SetTexture(mat, target, poolKey, rgba, width, height);
            rebound++;
        }
        return rebound;
    }

    public void SetHighlightRanges(IReadOnlyList<(int Start, int Count)>? ranges)
    {
        if (ReferenceEquals(_highlightSource, ranges)) return;
        _highlightSource = ranges;
        _highlight.Clear();
        if (ranges is null) return;
        foreach (var r in ranges)
            if (r.Count > 0 && r.Start >= 0) _highlight.Add(r);
    }

    private const string OverlayHlsl = @"
cbuffer OverlayCB : register(b0)
{
    row_major float4x4 gMvp;
    float4 gColor;
};
struct VIn { float3 pos : POSITION; };
float4 vsmain(VIn i) : SV_Position { return mul(float4(i.pos, 1.0), gMvp); }
float4 psmain() : SV_Target { return gColor; }

// Textured variant, for the placement glyphs. M657: the painted icons carry their own COLOUR as well as
// their shape, so gColor multiplies through rather than replacing the texture - substituting a flat tint
// (what this did while the glyphs were white-with-alpha) would throw the artwork away. A white gColor is
// therefore 'draw the art as paintedgColor.a is still the opacity.
struct VTexIn  { float3 pos : POSITION; float2 uv : TEXCOORD0; };
struct VTexOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
Texture2D gGlyph : register(t0);
SamplerState gGlyphSamp : register(s0);
VTexOut vsmain_tex(VTexIn i)
{
    VTexOut o;
    o.pos = mul(float4(i.pos, 1.0), gMvp);
    o.uv = i.uv;
    return o;
}
float4 psmain_tex(VTexOut i) : SV_Target
{
    float4 g = gGlyph.Sample(gGlyphSamp, i.uv);
    return float4(g.rgb * gColor.rgb, gColor.a * g.a);
}
";

    /// <summary>Compile the overlay pipeline once. Failure is remembered so a broken HLSL compiler costs
    /// one attempt rather than one per frame, and it is never fatal - the scene still renders without
    /// its furniture.</summary>
    private bool EnsureOverlay()
    {
        if (_overlayTried) return _overlayVs.Handle is not null;
        _overlayTried = true;

        ID3D10Blob* vsCode = null, psCode = null, errs = null;
        var src = System.Text.Encoding.ASCII.GetBytes(OverlayHlsl);
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* sp = src)
            {
                var vsEntry = System.Text.Encoding.ASCII.GetBytes("vsmain\0");
                var vsTarget = System.Text.Encoding.ASCII.GetBytes("vs_5_0\0");
                fixed (byte* ep = vsEntry) fixed (byte* tp = vsTarget)
                    if (compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                            ep, tp, 0u, 0u, &vsCode, &errs) < 0 || vsCode is null)
                    { Log("overlay vs failed to compile"); return false; }

                var psEntry = System.Text.Encoding.ASCII.GetBytes("psmain\0");
                var psTarget = System.Text.Encoding.ASCII.GetBytes("ps_5_0\0");
                fixed (byte* ep = psEntry) fixed (byte* tp = psTarget)
                    if (compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                            ep, tp, 0u, 0u, &psCode, &errs) < 0 || psCode is null)
                    { Log("overlay ps failed to compile"); return false; }
            }
        }
        catch (Exception ex) { Log("overlay: the HLSL compiler is unavailable: " + ex.Message); return false; }

        ComPtr<ID3D11VertexShader> vs = default;
        if (_device.CreateVertexShader(vsCode->GetBufferPointer(), vsCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vs) < 0) { Log("overlay CreateVertexShader failed"); return false; }
        _overlayVs = vs;

        ComPtr<ID3D11PixelShader> ps = default;
        if (_device.CreatePixelShader(psCode->GetBufferPointer(), psCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref ps) < 0) { Log("overlay CreatePixelShader failed"); return false; }
        _overlayPs = ps;

        // POSITION alone, read out of the same fat vertex the scene already uses - so the highlight draws
        // the identical geometry with the identical stride and cannot drift from what it is highlighting.
        var semantic = System.Text.Encoding.ASCII.GetBytes("POSITION\0");
        fixed (byte* sem = semantic)
        {
            var el = new InputElementDesc
            {
                SemanticName = sem, SemanticIndex = 0,
                Format = Format.FormatR32G32B32Float, InputSlot = 0, AlignedByteOffset = 0,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            ComPtr<ID3D11InputLayout> layout = default;
            if (_device.CreateInputLayout(&el, 1, vsCode->GetBufferPointer(), vsCode->GetBufferSize(), ref layout) < 0)
            { Log("overlay CreateInputLayout failed"); return false; }
            _overlayLayout = layout;
        }

        var cbDesc = new BufferDesc
        {
            ByteWidth = 80,                      // float4x4 + float4
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> cb = default;
        if (_device.CreateBuffer(in cbDesc, null, ref cb) < 0) { Log("overlay cbuffer failed"); return false; }
        _overlayCb = cb;

        // Depth test ON, write OFF: the highlight should be occluded by geometry in front of it - a
        // selection glowing through a wall would misreport where the thing actually is - but it must not
        // disturb the depth buffer for anything drawn afterwards.
        var dsd = new DepthStencilDesc
        {
            DepthEnable = 1, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.LessEqual,
        };
        ComPtr<ID3D11DepthStencilState> ds = default;
        _device.CreateDepthStencilState(in dsd, ref ds);
        _overlayDepth = ds;

        // The overlay recomputes clip position with its own vertex shader, so its depth does not land
        // bit-identically on what Riot's shader wrote for the same triangle. At equal depth LessEqual is
        // a coin toss, and the highlight loses. This is the escape hatch, and which one is needed is a
        // measurement rather than a guess - see the `highlight` harness mode.
        var dsdNoTest = dsd; dsdNoTest.DepthEnable = 0;
        ComPtr<ID3D11DepthStencilState> dsn = default;
        _device.CreateDepthStencilState(in dsdNoTest, ref dsn);
        _overlayDepthNoTest = dsn;

        var bd = new BlendDesc();
        bd.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = Blend.SrcAlpha, DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.InvSrcAlpha, BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ComPtr<ID3D11BlendState> bs = default;
        _device.CreateBlendState(in bd, ref bs);
        _overlayBlend = bs;

        // The textured pair, for glyphs. Compiled in the same pass so a failure here is reported with
        // the rest rather than surfacing later as markers that are silently square.
        ID3D10Blob* vsT = null, psT = null;
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* sp = src)
            {
                var e1 = System.Text.Encoding.ASCII.GetBytes("vsmain_tex\0");
                var t1 = System.Text.Encoding.ASCII.GetBytes("vs_5_0\0");
                fixed (byte* ep = e1) fixed (byte* tp = t1)
                    if (compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                            ep, tp, 0u, 0u, &vsT, &errs) < 0 || vsT is null)
                    { Log("overlay textured vs failed to compile"); return true; }

                var e2 = System.Text.Encoding.ASCII.GetBytes("psmain_tex\0");
                var t2 = System.Text.Encoding.ASCII.GetBytes("ps_5_0\0");
                fixed (byte* ep = e2) fixed (byte* tp = t2)
                    if (compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                            ep, tp, 0u, 0u, &psT, &errs) < 0 || psT is null)
                    { Log("overlay textured ps failed to compile"); return true; }
            }
        }
        catch { Log("overlay textured pair unavailable"); return true; }

        ComPtr<ID3D11VertexShader> vst = default;
        _device.CreateVertexShader(vsT->GetBufferPointer(), vsT->GetBufferSize(),
            ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vst);
        _overlayVsTex = vst;
        ComPtr<ID3D11PixelShader> pst = default;
        _device.CreatePixelShader(psT->GetBufferPointer(), psT->GetBufferSize(),
            ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref pst);
        _overlayPsTex = pst;

        // POSITION and TEXCOORD0 out of the same fat vertex. Uv0 is a float4 there; declaring two
        // components simply ignores the rest, so no new vertex format is needed for glyphs.
        var semPos = System.Text.Encoding.ASCII.GetBytes("POSITION\0");
        var semUv = System.Text.Encoding.ASCII.GetBytes("TEXCOORD\0");
        fixed (byte* sp0 = semPos)
        fixed (byte* sp1 = semUv)
        {
            var els = stackalloc InputElementDesc[2];
            els[0] = new InputElementDesc
            {
                SemanticName = sp0, SemanticIndex = 0, Format = Format.FormatR32G32B32Float,
                InputSlot = 0, AlignedByteOffset = 0,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            els[1] = new InputElementDesc
            {
                SemanticName = sp1, SemanticIndex = 0, Format = Format.FormatR32G32Float,
                InputSlot = 0, // PreviewVertex.Uv0 sits at +40; the same offset the material path maps TEXCOORD0 to.
                AlignedByteOffset = 40,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            ComPtr<ID3D11InputLayout> lt = default;
            _device.CreateInputLayout(els, 2, vsT->GetBufferPointer(), vsT->GetBufferSize(), ref lt);
            _overlayLayoutTex = lt;
        }

        var sd = new SamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MaxLOD = float.MaxValue,
        };
        ComPtr<ID3D11SamplerState> smp = default;
        _device.CreateSamplerState(in sd, ref smp);
        _iconSampler = smp;

        // M658: solid gizmo handles, drawn with no culling - see DrawGizmo.
        if (_gizmoRaster.Handle is null)
        {
            var grd = new RasterizerDesc
            {
                FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = 1,
            };
            ComPtr<ID3D11RasterizerState> grs = default;
            if (_device.CreateRasterizerState(in grd, ref grs) >= 0) _gizmoRaster = grs;
        }

        for (int g = 0; g < _glyphSrv.Length; g++)
        {
            // M657: painted art when it decodes, the M271 drawn glyph when it does not. MakeTexture
            // builds the full mip chain (M294), which 256px art drawn at a few dozen pixels needs.
            var (rgba, size) = IconGlyphs.Load((ViewportIcon)g);
            var srv = MakeTexture(rgba, size, size);
            if (srv is { } v) _glyphSrv[g] = v;
        }

        Log("overlay pipeline built");
        return true;
    }

    /// <summary>Draw the highlighted ranges over the finished frame. Returns the number of draws made.</summary>
    private int DrawHighlight(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_highlight.Count == 0 || _vb.Handle is null || _wireIb.Handle is null) return 0;
        if (!EnsureOverlay()) return 0;

        // view already carries the X mirror when MirrorX is on (applied at the top of the draw), so the
        // overlay lands on the same pixels as the geometry it is marking rather than its reflection.
        var mvp = Matrix4x4.Multiply(view, proj);
        SetOverlayCb(mvp, HighlightColor);

        uint stride = PreviewVertex.SizeInBytes, offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _vb, in stride, in offset);
        // M381: the EDGE buffer, drawn as lines - GL's outline, not a translucent fill over the faces.
        _ctx.IASetIndexBuffer(_wireIb, Format.FormatR32Uint, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyLinelist);
        _ctx.IASetInputLayout(_overlayLayout);
        _ctx.VSSetShader(_overlayVs, null, 0);
        _ctx.PSSetShader(_overlayPs, null, 0);
        _ctx.VSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.OMSetDepthStencilState(HighlightDepthTest ? _overlayDepth : _overlayDepthNoTest, 0);
        var factor = stackalloc float[4] { 0, 0, 0, 0 };
        _ctx.OMSetBlendState(_overlayBlend, factor, 0xFFFFFFFF);

        int draws = 0;
        foreach (var (start, count) in _highlight)
        {
            if (start + count > _indexCount) continue;   // a stale range from a previous map
            // Triangle range -> edge range: same mapping ViewportMeshRenderer applies to its _wireEbo.
            _ctx.DrawIndexed((uint)(count * 2), (uint)(start * 2), 0);
            draws++;
        }
        // Restore triangles for whatever draws next - the overlay stages that follow assume it.
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        return draws;
    }

    /// <summary>The generated HLSL, so the window can show exactly what it is being compared against.</summary>
    public string? ComparisonShaderSource { get; private set; }

    // ---------------------------------------------------------------- resources

    /// <summary>M232: true when the current buffers were made writable by <see cref="SetDynamicMesh"/>.</summary>
    private bool _dynamicMesh;
    private int _dynVbCapacity, _dynIbCapacity;

    /// <summary>
    /// <para>M232: allocate DYNAMIC vertex and index buffers big enough for <paramref name="maxVertices"/>,
    /// so animated particle geometry can be rewritten every frame with Map/WriteDiscard instead of
    /// recreating a buffer per frame.</para>
    ///
    /// <para>Re-allocates only when the request outgrows what is already there, so a system whose particle
    /// count fluctuates settles on one allocation rather than thrashing.</para>
    /// </summary>
    public void SetDynamicMesh(int maxVertices, int maxIndices)
    {
        if (_dynamicMesh && maxVertices <= _dynVbCapacity && maxIndices <= _dynIbCapacity) return;

        _dynVb.Dispose(); _dynIb.Dispose();
        _dynVb = default; _dynIb = default;
        _dynVbCapacity = Math.Max(maxVertices, 4);
        _dynIbCapacity = Math.Max(maxIndices, 6);

        var vdesc = new BufferDesc
        {
            ByteWidth = (uint)(_dynVbCapacity * PreviewVertex.SizeInBytes),
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.VertexBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> vb = default;
        _device.CreateBuffer(in vdesc, null, ref vb);
        _dynVb = vb;

        var idesc = new BufferDesc
        {
            ByteWidth = (uint)(_dynIbCapacity * sizeof(uint)),
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.IndexBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> ib = default;
        _device.CreateBuffer(in idesc, null, ref ib);
        _dynIb = ib;

        _dynamicMesh = true;
        _dynIndexCount = 0;
        // M264: does NOT clear Mesh. This pair sits alongside the static scene now rather than replacing
        // it, and nulling the static mesh here is what used to make the map vanish behind particles.
    }

    /// <summary>M232: overwrite the dynamic buffers for this frame. Silently no-ops when the buffers are
    /// immutable, so a caller that forgot SetDynamicMesh gets a still frame rather than a device removal.</summary>
    public void UpdateDynamicMesh(PreviewVertex[] vertices, int vertexCount, uint[] indices, int indexCount)
    {
        if (!_dynamicMesh || _dynVb.Handle is null || _dynIb.Handle is null) return;
        vertexCount = Math.Min(vertexCount, _dynVbCapacity);
        indexCount = Math.Min(indexCount, _dynIbCapacity);

        MappedSubresource mv = default;
        if (_ctx.Map(_dynVb, 0, Map.WriteDiscard, 0, ref mv) >= 0)
        {
            fixed (PreviewVertex* src = vertices)
                System.Buffer.MemoryCopy(src, mv.PData,
                    (long)_dynVbCapacity * PreviewVertex.SizeInBytes,
                    (long)vertexCount * PreviewVertex.SizeInBytes);
            _ctx.Unmap(_dynVb, 0);
        }

        MappedSubresource mi = default;
        if (_ctx.Map(_dynIb, 0, Map.WriteDiscard, 0, ref mi) >= 0)
        {
            fixed (uint* src = indices)
                System.Buffer.MemoryCopy(src, mi.PData, (long)_dynIbCapacity * sizeof(uint),
                    (long)indexCount * sizeof(uint));
            _ctx.Unmap(_dynIb, 0);
        }

        _dynIndexCount = indexCount;
    }

    /// <summary>
    /// <para>M264: point the input assembler at whichever buffer pair this draw reads from, rebinding only
    /// when a draw crosses between them - so a frame of map geometry followed by particles costs one extra
    /// bind, not one per slice.</para>
    ///
    /// <para>Returns false when the requested pair does not exist yet. That is a real case rather than a
    /// defensive one: particle materials are registered when their pipelines resolve, which is before any
    /// quad has been uploaded, and drawing from a null buffer would take the device down.</para>
    /// </summary>
    private bool BindMeshSource(bool dynamic, ref int bound)
    {
        if (dynamic ? _dynVb.Handle is null || _dynIb.Handle is null
                    : _vb.Handle is null || _ib.Handle is null) return false;
        int want = dynamic ? 1 : 0;
        if (want == bound) return true;

        uint stride = PreviewVertex.SizeInBytes, offset = 0;
        if (dynamic)
        {
            _ctx.IASetVertexBuffers(0, 1, ref _dynVb, in stride, in offset);
            _ctx.IASetIndexBuffer(_dynIb, Format.FormatR32Uint, 0);
        }
        else
        {
            _ctx.IASetVertexBuffers(0, 1, ref _vb, in stride, in offset);
            _ctx.IASetIndexBuffer(_ib, Format.FormatR32Uint, 0);
        }
        bound = want;
        return true;
    }

    public void SetMesh(PreviewMesh mesh)
    {
        // M264: deliberately does NOT touch the dynamic pair - loading a map must not silently drop
        // the particles drawn on top of it.
        Mesh = mesh;
        _vb.Dispose(); _ib.Dispose(); _wireIb.Dispose();
        _vb = default; _ib = default; _wireIb = default;

        var vdesc = new BufferDesc
        {
            ByteWidth = (uint)(mesh.Vertices.Length * PreviewVertex.SizeInBytes),
            Usage = Usage.Default, BindFlags = (uint)BindFlag.VertexBuffer,
        };
        fixed (PreviewVertex* p = mesh.Vertices)
        {
            var sub = new SubresourceData { PSysMem = p };
            ComPtr<ID3D11Buffer> b = default;
            _device.CreateBuffer(in vdesc, in sub, ref b);
            _vb = b;
        }

        var idesc = new BufferDesc
        {
            ByteWidth = (uint)(mesh.Indices.Length * 4),
            Usage = Usage.Immutable, BindFlags = (uint)BindFlag.IndexBuffer,
        };
        fixed (uint* p = mesh.Indices)
        {
            var sub = new SubresourceData { PSysMem = p };
            ComPtr<ID3D11Buffer> b = default;
            _device.CreateBuffer(in idesc, in sub, ref b);
            _ib = b;
        }
        _indexCount = mesh.Indices.Length;

        // M381: the edge buffer behind the wireframe selection outline. Two indices per triangle edge,
        // laid out so wire offset == triangle offset * 2 (see _wireIb).
        int triCount = mesh.Indices.Length / 3;
        if (triCount > 0)
        {
            var wire = new uint[triCount * 6];
            for (int t = 0; t < triCount; t++)
            {
                uint a = mesh.Indices[t * 3], b2 = mesh.Indices[t * 3 + 1], c = mesh.Indices[t * 3 + 2];
                int o = t * 6;
                wire[o] = a; wire[o + 1] = b2;
                wire[o + 2] = b2; wire[o + 3] = c;
                wire[o + 4] = c; wire[o + 5] = a;
            }
            var wdesc = new BufferDesc
            {
                ByteWidth = (uint)(wire.Length * 4),
                Usage = Usage.Immutable, BindFlags = (uint)BindFlag.IndexBuffer,
            };
            fixed (uint* p = wire)
            {
                var sub = new SubresourceData { PSysMem = p };
                ComPtr<ID3D11Buffer> b = default;
                _device.CreateBuffer(in wdesc, in sub, ref b);
                _wireIb = b;
            }
        }

        Log($"mesh '{mesh.Name}': {mesh.Vertices.Length:n0} verts, {mesh.TriangleCount:n0} tris");
    }

    /// <summary>Update an edited mapgeo vertex range without rebuilding shaders or textures.</summary>
    public void UpdateMeshVertices(float[] positions, float[] normals, int startVertex, int vertexCount)
    {
        if (Mesh is null || _vb.Handle is null || vertexCount <= 0) return;
        int start = Math.Clamp(startVertex, 0, Mesh.Vertices.Length);
        int end = Math.Clamp(start + vertexCount, start, Mesh.Vertices.Length);
        end = Math.Min(end, Math.Min(positions.Length / 3, normals.Length / 3));
        if (end <= start) return;

        for (int v = start; v < end; v++)
        {
            Mesh.Vertices[v].Position = new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
            Mesh.Vertices[v].Normal = new Vector3(normals[v * 3], normals[v * 3 + 1], normals[v * 3 + 2]);
        }

        var box = new Box
        {
            Left = (uint)(start * PreviewVertex.SizeInBytes),
            Right = (uint)(end * PreviewVertex.SizeInBytes),
            Top = 0,
            Bottom = 1,
            Front = 0,
            Back = 1,
        };
        fixed (PreviewVertex* p = &Mesh.Vertices[start])
            _ctx.UpdateSubresource(_vb, 0, &box, p, 0, 0);

        // Edited vertices invalidate the per-material culling boxes. Drawing without a box is correct;
        // retaining the old one can incorrectly cull a mesh moved outside its former bounds.
        foreach (var material in _materials) material.Bounds = null;
    }

    /// <summary>M226: decoded textures, keyed by the asset path they came from and shared by every
    /// material that wants them. The renderer owns these; <see cref="PreviewMaterial"/> only references
    /// them.</summary>
    private readonly Dictionary<string, ComPtr<ID3D11ShaderResourceView>> _texPool = new(StringComparer.Ordinal);

    /// <summary>Bind an already-decoded asset from the pool. Returns false when it has not been decoded yet,
    /// which is the caller's cue to read and decode it - and the point of the whole thing, because on a hit
    /// neither the WAD read nor the decode happens. A Map12 load decoded one 2048 lightmap 282 times at
    /// 35.8 ms each before this existed.</summary>
    /// <summary>M244: is this texture already resident on the GPU? Lets the off-thread pre-decode skip
    /// work the renderer would only throw away.</summary>
    public bool HasCachedTexture(string key) => _texPool.ContainsKey(key);

    public bool TryBindCached(PreviewMaterial m, string reflectedName, string key)
    {
        if (!_texPool.TryGetValue(key, out var srv) || srv.Handle is null) return false;
        m.Textures[reflectedName] = srv;
        RecordDebugSlot(m, reflectedName, srv);   // M661
        return true;
    }

    public bool IsCached(string key) => _texPool.TryGetValue(key, out var v) && v.Handle is not null;

    /// <summary>M360: overwrite a pooled texture's pixels in place, for live brush strokes.
    ///
    /// <para>In place, deliberately. Materials hold the SRV itself (<c>m.Textures[name] = srv</c>), not the
    /// pool key, so creating a replacement SRV and swapping the pool entry would leave every already-bound
    /// material pointing at the old one - the paint would land in the pool and never appear. Writing
    /// through the existing resource keeps every binding valid and updates all users at once.</para>
    ///
    /// <para>Whole-texture rather than the dirty rect: one upload of a 2048 map texture is ~16 MB and the
    /// stroke handler is already throttled. A sub-region write is the obvious optimisation, but only worth
    /// making once it has been measured as too slow.</para>
    ///
    /// <para>Returns false rather than throwing on anything unexpected - a texture that was created
    /// Immutable (M294 does that for unmipped ones) cannot be written, and a brush stroke that does not
    /// show in the D3D11 view is a far better outcome than one that takes the editor down.</para></summary>
    public bool UpdatePooledTexture(string key, byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) return false;
        if (!_texPool.TryGetValue(key, out var srv) || srv.Handle is null) return false;

        ID3D11Resource* res = null;
        try
        {
            srv.GetResource(ref res);
            if (res is null) return false;
            // (Box*)null, not bare null: the Span overload is otherwise ambiguous. Null box = whole resource.
            fixed (byte* p = rgba)
                _ctx.UpdateSubresource(res, 0, (Box*)null, p, (uint)(width * 4), 0);
            return true;
        }
        catch { return false; }
        finally { if (res is not null) res->Release(); }
    }

    /// <summary>M360: regenerate mips after a stroke finishes. Only textures created with a mip chain
    /// (M294: <c>MipLevels = 0</c> plus <c>GenerateMips</c>) have anything to rebuild; the call is harmless
    /// on the others, and skipping it leaves a painted surface sharp up close and stale at distance.</summary>
    public void RegeneratePooledMips(string key)
    {
        if (!_texPool.TryGetValue(key, out var srv) || srv.Handle is null) return;
        try { _ctx.GenerateMips(srv); } catch { }
    }

    /// <summary>M275: the authored sun direction as a unit vector, for upload to SUN_LIGHT_DIRECTION.
    /// A zero vector is passed through rather than normalised - Vector3.Normalize would hand back NaN and
    /// NaN in a dp3 poisons the whole pixel, where zero just means "no sun", which is a survivable answer to
    /// a map that authored nothing.</summary>
    private static float[] UnitSun(Vector3 d)
    {
        float len = d.Length();
        if (len < 1e-6f) return new[] { d.X, d.Y, d.Z, 0f };
        return new[] { d.X / len, d.Y / len, d.Z / len, 0f };
    }

    /// <summary>M273: does the pooled asset under this key use its alpha channel? Recorded as a side effect
    /// of decoding, because modes 2 and 3 pick their blend state from it (VfxShaderFlags.IsAdditive) and the
    /// pipeline has to know BEFORE it builds - by which point a pool hit means nobody has the pixels any
    /// more. Keyed and cleared exactly like <see cref="_texPool"/>, so the two cannot fall out of step.</summary>
    private readonly Dictionary<string, bool> _texAlpha = new(StringComparer.Ordinal);

    public bool TryGetTextureAlpha(string key, out bool hasAlpha) => _texAlpha.TryGetValue(key, out hasAlpha);

    /// <summary>Record what a caller decoded elsewhere. Only needed when pixels reach the pool by a route
    /// that skips <see cref="SetTexture"/>; the normal path records itself.</summary>
    public void NoteTextureAlpha(string key, bool hasAlpha) => _texAlpha[key] = hasAlpha;

    /// <summary>Bind RGBA8 pixels to a reflected texture name on EVERY material that declares it. That is
    /// what the bench wants; a scene binds per material instead.</summary>
    public void SetTexture(string reflectedName, byte[] rgba, int width, int height)
    {
        foreach (var m in _materials)
        {
            uint dimension = m.PsRefl.Textures.Concat(m.VsRefl.Textures)
                .FirstOrDefault(t => t.Name.Equals(reflectedName, StringComparison.OrdinalIgnoreCase))?.Dimension ?? 4;
            SetTextureCore(m, reflectedName, $"\0bench:{reflectedName}:{dimension}",
                rgba, width, height, dimension);
        }
        if (_materials.Count > 0) Log($"bound texture '{reflectedName}' ({width}x{height})");
    }

    /// <summary>Bench path: no asset identity, so it gets a private pool entry per slot.</summary>
    public void SetTexture(PreviewMaterial m, string reflectedName, byte[] rgba, int width, int height)
        => SetTexture(m, reflectedName, "\u0000bench:" + reflectedName, rgba, width, height);

    /// <summary>Scene path: <paramref name="key"/> is the asset path, so the view is created once and
    /// shared.</summary>
    public void SetTexture(PreviewMaterial m, string reflectedName, string key, byte[] rgba, int width, int height)
        => SetTextureCore(m, reflectedName, key, rgba, width, height, resourceDimension: 4);

    /// <summary>M321: bind one decoded image through a Texture2DArray SRV. Riot's map-wide
    /// TERRAIN_BLEND_SharedTexture is declared as a 2D array even when the map ships only one slice.</summary>
    public void SetTextureArray(PreviewMaterial m, string reflectedName, string key, byte[] rgba, int width, int height)
        => SetTextureCore(m, reflectedName, key, rgba, width, height, resourceDimension: 5);

    private void SetTextureCore(PreviewMaterial m, string reflectedName, string key,
        byte[] rgba, int width, int height, uint resourceDimension)
    {
        // ALWAYS create. Skipping the work on a cache hit is the CALLER's job, via TryBindCached - if this
        // reused the pooled view whenever the key existed, re-binding a different image to the same slot
        // from the Textures tab would silently keep showing the old one.
        var made = MakeTexture(rgba, width, height, resourceDimension: resourceDimension);
        if (made is null) return;                           // creation failed and was reported

        // A view already under this key may still be referenced by materials bound earlier, so it is
        // retired rather than disposed here; the whole set goes at ClearTextures.
        if (_texPool.TryGetValue(key, out var previous) && previous.Handle is not null) _retired.Add(previous);

        _texPool[key] = made.Value;
        _texAlpha[key] = Formats.Vfx.VfxShaderFlags.TextureUsesAlpha(rgba);
        m.Textures[reflectedName] = made.Value;
        RecordDebugSlot(m, reflectedName, made.Value);   // M661
    }

    /// <summary>Views replaced while something might still hold them. Freed with the rest of the pool.</summary>
    private readonly List<ComPtr<ID3D11ShaderResourceView>> _retired = new();

    public void ClearTextures()
    {
        foreach (var m in _materials) m.Textures.Clear();
        foreach (var t in _texPool.Values) t.Dispose();
        foreach (var t in _retired) t.Dispose();
        _texPool.Clear();
        _texAlpha.Clear();
        _retired.Clear();
    }

    /// <summary>Release exact scene-owned pool entries after their materials have been removed.</summary>
    public void RemoveCachedTextures(IEnumerable<string> keys)
    {
        foreach (string key in keys.Distinct(StringComparer.Ordinal))
        {
            if (_texPool.Remove(key, out var texture)) texture.Dispose();
            _texAlpha.Remove(key);
        }
    }

    /// <summary>How many distinct assets are resident, for the scene report.</summary>
    public int CachedTextureCount => _texPool.Count;

    /// <summary>M226: returns null on failure instead of a null-handle view. Neither HRESULT was checked
    /// before, so a failed creation still got stored under its key and then bound as a null resource -
    /// which samples BLACK rather than falling through to the white stand-in. BuildMaterial already checks
    /// its shader HRESULTs; this was the one place that did not.</summary>
    private ComPtr<ID3D11ShaderResourceView>? MakeTexture(
        byte[] rgba, int w, int h, uint resourceDimension = 4,
        // M366: overridable so a DEPTH stand-in can be R32_FLOAT. Everything else stays RGBA8; the
        // 4-bytes-per-texel assumptions below (length check, SysMemPitch) hold for both formats.
        Format format = Format.FormatR8G8B8A8Unorm)
    {
        if (w <= 0 || h <= 0 || rgba.Length < w * h * 4)
        {
            Log($"texture rejected: {w}x{h} needs {(long)w * h * 4} bytes, got {rgba.Length}");
            return null;
        }

        // M294: a full MIP CHAIN. These were created MipLevels = 1, so however far the camera flew the
        // sampler still read the top level - a texel shrinking below a pixel with nothing to filter it
        // against, which is minification aliasing and reads as "the map gets pixelated when I fly away".
        // The sampler was already asking for trilinear (Filter.MinMagMipLinear); there was simply nothing
        // for it to filter between. The GL viewport has always built mips here
        // (ViewportMeshRenderer GenerateMipmap + LinearMipmapLinear), which is why this was DX11-only.
        //
        // GenerateMips dictates the rest of the description: it needs a render-target bind, the
        // GenerateMips misc flag and Usage.Default, so the texture can no longer be Immutable and mip 0
        // is uploaded after creation rather than as initial data.
        bool cube = resourceDimension is 9 or 10;
        uint arraySize = cube ? 6u : 1u;
        bool mipped = w > 1 && h > 1;
        uint mipCount = 1;
        for (int mw = w, mh = h; mipped && (mw > 1 || mh > 1);)
        {
            mw = Math.Max(1, mw / 2); mh = Math.Max(1, mh / 2); mipCount++;
        }
        var desc = new Texture2DDesc
        {
            Width = (uint)w, Height = (uint)h,
            MipLevels = mipped ? 0u : 1u,          // 0 = full chain down to 1x1
            ArraySize = arraySize,
            Format = format, SampleDesc = new SampleDesc(1, 0),
            Usage = mipped || cube ? Usage.Default : Usage.Immutable,
            BindFlags = (uint)(mipped ? BindFlag.ShaderResource | BindFlag.RenderTarget
                                      : BindFlag.ShaderResource),
            MiscFlags = (uint)((mipped ? ResourceMiscFlag.GenerateMips : 0)
                               | (cube ? ResourceMiscFlag.Texturecube : 0)),
        };

        ComPtr<ID3D11Texture2D> tex = default;
        int hr;
        if (mipped || cube)
        {
            hr = _device.CreateTexture2D(in desc, null, ref tex);
            if (hr >= 0)
                fixed (byte* p = rgba)
                    for (uint slice = 0; slice < arraySize; slice++)
                        _ctx.UpdateSubresource(tex, slice * mipCount, (Box*)null, p, (uint)(w * 4), 0u);
        }
        else
        {
            fixed (byte* p = rgba)
            {
                var sub = new SubresourceData { PSysMem = p, SysMemPitch = (uint)(w * 4) };
                hr = _device.CreateTexture2D(in desc, in sub, ref tex);
            }
        }
        if (hr < 0) { Log($"CreateTexture2D failed 0x{hr:X8} for {w}x{h}"); return null; }

        ComPtr<ID3D11ShaderResourceView> srv = default;
        if (resourceDimension == 5)
        {
            var srvDesc = new ShaderResourceViewDesc
            {
                Format = desc.Format,
                ViewDimension = D3DSrvDimension.D3D11SrvDimensionTexture2Darray,
                Anonymous = new ShaderResourceViewDescUnion
                {
                    Texture2DArray = new Tex2DArraySrv
                    {
                        MostDetailedMip = 0,
                        MipLevels = mipped ? uint.MaxValue : 1u,
                        FirstArraySlice = 0,
                        ArraySize = 1,
                    },
                },
            };
            hr = _device.CreateShaderResourceView(tex, in srvDesc, ref srv);
        }
        else if (resourceDimension == 9)
        {
            var srvDesc = new ShaderResourceViewDesc
            {
                Format = desc.Format,
                ViewDimension = (D3DSrvDimension)9,
                Anonymous = new ShaderResourceViewDescUnion
                {
                    TextureCube = new TexcubeSrv
                    {
                        MostDetailedMip = 0,
                        MipLevels = mipped ? uint.MaxValue : 1u,
                    },
                },
            };
            hr = _device.CreateShaderResourceView(tex, in srvDesc, ref srv);
        }
        else if (resourceDimension == 10)
        {
            var srvDesc = new ShaderResourceViewDesc
            {
                Format = desc.Format,
                ViewDimension = (D3DSrvDimension)10,
                Anonymous = new ShaderResourceViewDescUnion
                {
                    TextureCubeArray = new TexcubeArraySrv
                    {
                        MostDetailedMip = 0,
                        MipLevels = mipped ? uint.MaxValue : 1u,
                        First2DArrayFace = 0,
                        NumCubes = 1,
                    },
                },
            };
            hr = _device.CreateShaderResourceView(tex, in srvDesc, ref srv);
        }
        else hr = _device.CreateShaderResourceView(tex, null, ref srv);
        tex.Dispose();
        if (hr < 0) { Log($"CreateShaderResourceView failed 0x{hr:X8} for {w}x{h}"); return null; }
        if (mipped) _ctx.GenerateMips(srv);
        return srv;
    }

    /// <summary>M283: mesh-primitive particles, ported from the GL mesh program
    /// (<c>VfxParticleRenderer.cs:1310-1413</c>).
    ///
    /// <para>The transform is the GL one exactly: a Y-axis spin by <c>rot</c>, a UNIFORM scalar scale (the
    /// mesh path uses birthScale.x alone - Y is not read), then composition against the placement's three
    /// normalised basis vectors. Because those are normalised, the placement's SCALE is discarded and only
    /// its rotation survives - that is GL's behaviour and matching it matters more than being right.</para>
    ///
    /// <para>Not ported: fresnel and the reflection cubemap. Both need per-vertex normals, which the
    /// StaticMeshData this path receives does not carry (the .skn decoder drops them and GL recomputes
    /// them from face winding), plus a cubemap SRV that nothing resolves on this side. They affect a
    /// subset of mesh emitters and are called out rather than silently approximated.</para></summary>
    private const string MeshHlsl = @"
cbuffer MeshCB : register(b0)
{
    row_major float4x4 gViewProj;
    row_major float4x4 gModel;   // M295: props supply a real transform; particles leave it identity
    float4 gRight;      // xyz = placement right
    float4 gUp;
    float4 gForward;
    float4 gPosScale;   // xyz = world position, w = uniform scale
    float4 gColor;
    float4 gUv;         // xy = scroll offset, zw = tiling
    float4 gUvMult;
    float4 gMisc;       // x = rotation (radians, Y axis), y = has texMult, z = alpha cutoff
};
Texture2D gTex     : register(t0);
Texture2D gTexMult : register(t1);
SamplerState gSamp : register(s0);

struct VIn  { float3 pos : POSITION; float2 uv : TEXCOORD0; };
struct VOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float2 uvMult : TEXCOORD1; };

VOut vsmain(VIn i)
{
    VOut o;
    float s = sin(gMisc.x);
    float c = cos(gMisc.x);
    float3 local = float3(i.pos.x * c - i.pos.z * s, i.pos.y, i.pos.x * s + i.pos.z * c) * gPosScale.w;
    float3 p = gRight.xyz * local.x + gUp.xyz * local.y + gForward.xyz * local.z + gPosScale.xyz;
    // M295: a prop's placement is an arbitrary 4x4 out of the map's .materials.bin - rotation, non-uniform
    // scale and shear - which the particle path's basis+scalar-scale composition above cannot express. A
    // particle leaves this identity and is unaffected.
    p = mul(float4(p, 1.0), gModel).xyz;
    o.pos = mul(float4(p, 1.0), gViewProj);
    o.uv     = i.uv * max(gUv.zw, 0.0001) + gUv.xy;
    o.uvMult = i.uv * max(gUvMult.zw, 0.0001) + gUvMult.xy;
    return o;
}

float4 psmain(VOut i) : SV_Target
{
    float4 t = gTex.Sample(gSamp, i.uv);
    if (gMisc.y != 0.0) t *= gTexMult.Sample(gSamp, i.uvMult);
    // M297: alpha CUTOUT for props. They write depth, so blending their fringes would stamp depth for
    // near-transparent texels and halo everything behind - fur and wings especially. GL cuts these at
    // 0.35 for the same reason. Particles leave the cutoff at 0 and keep blending, as they must.
    if (gMisc.z > 0.0 && t.a < gMisc.z) discard;
    return t * gColor;
}";

    /// <summary>M293: the bucket grid, matching what the GL viewport actually draws.
    ///
    /// <para>GL does NOT draw lines for this - it draws a triangle soup carrying barycentric coordinates
    /// and discards the interior, which gives a full wireframe at triangle-raster cost
    /// (ViewportMeshRenderer BucketWireVert/BucketWireFrag). Porting the look means porting that trick, so
    /// this is the same barycentric edge test with the same clip-space depth bias, not a line list.</para></summary>
    private const string GridHlsl = @"
cbuffer GridCB : register(b0)
{
    row_major float4x4 gMvp;
    float4 gColor;
};
struct VIn  { float3 pos : POSITION; float2 bary : TEXCOORD0; };
struct VOut { float4 pos : SV_Position; float3 bary : TEXCOORD0; };

VOut vsmain(VIn i)
{
    VOut o;
    o.pos = mul(float4(i.pos, 1.0), gMvp);
    // The same small bias GL applies, so the grid sits on the ground rather than z-fighting with it.
    o.pos.z -= 0.0006 * o.pos.w;
    o.bary = float3(i.bary, 1.0 - i.bary.x - i.bary.y);
    return o;
}

float4 psmain(VOut i) : SV_Target
{
    float3 d = fwidth(i.bary);
    float3 a = smoothstep(float3(0,0,0), d * 1.5, i.bary);
    float edge = min(min(a.x, a.y), a.z);
    if (edge > 0.95) discard;          // interior: keep only the wire
    return float4(gColor.rgb, gColor.a * (1.0 - edge));
}";

    private bool EnsureGrid()
    {
        if (_gridTried) return _gridVs.Handle is not null;
        _gridTried = true;

        ID3D10Blob* vsCode = null, psCode = null, errs = null;
        var src = System.Text.Encoding.ASCII.GetBytes(GridHlsl);
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* sp = src)
            {
                var e1 = System.Text.Encoding.ASCII.GetBytes("vsmain\0");
                var t1 = System.Text.Encoding.ASCII.GetBytes("vs_5_0\0");
                fixed (byte* ep = e1) fixed (byte* tp = t1)
                    if (compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                            ep, tp, 0u, 0u, &vsCode, &errs) < 0 || vsCode is null)
                    { Log("bucket grid vs failed to compile"); return false; }
                var e2 = System.Text.Encoding.ASCII.GetBytes("psmain\0");
                var t2 = System.Text.Encoding.ASCII.GetBytes("ps_5_0\0");
                fixed (byte* ep = e2) fixed (byte* tp = t2)
                    if (compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                            ep, tp, 0u, 0u, &psCode, &errs) < 0 || psCode is null)
                    { Log("bucket grid ps failed to compile"); return false; }
            }
        }
        catch (Exception ex) { Log("bucket grid: the HLSL compiler is unavailable: " + ex.Message); return false; }

        ComPtr<ID3D11VertexShader> vs = default;
        if (_device.CreateVertexShader(vsCode->GetBufferPointer(), vsCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vs) < 0) { Log("grid CreateVertexShader failed"); return false; }
        _gridVs = vs;
        ComPtr<ID3D11PixelShader> ps = default;
        if (_device.CreatePixelShader(psCode->GetBufferPointer(), psCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref ps) < 0) { Log("grid CreatePixelShader failed"); return false; }
        _gridPs = ps;

        var semPos = System.Text.Encoding.ASCII.GetBytes("POSITION\0");
        var semUv = System.Text.Encoding.ASCII.GetBytes("TEXCOORD\0");
        fixed (byte* sp0 = semPos)
        fixed (byte* sp1 = semUv)
        {
            var els = stackalloc InputElementDesc[2];
            els[0] = new InputElementDesc
            {
                SemanticName = sp0, SemanticIndex = 0, Format = Format.FormatR32G32B32Float,
                InputSlot = 0, AlignedByteOffset = 0,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            els[1] = new InputElementDesc
            {
                SemanticName = sp1, SemanticIndex = 0, Format = Format.FormatR32G32Float,
                InputSlot = 0, AlignedByteOffset = 12,   // pos3 then the first two barycentrics
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            ComPtr<ID3D11InputLayout> layout = default;
            if (_device.CreateInputLayout(els, 2, vsCode->GetBufferPointer(), vsCode->GetBufferSize(), ref layout) < 0)
            { Log("grid CreateInputLayout failed"); return false; }
            _gridLayout = layout;
        }

        Log("bucket grid pipeline built");
        return true;
    }

    /// <summary>Upload the grid's pos3+bary3 soup. Null or empty clears it. The payload is multi-megabyte,
    /// so callers should skip re-sending the same array - see the ReferenceEquals guard the GL host uses.</summary>
    public void SetBucketGrid(float[]? posBary)
    {
        _gridVertexCount = 0;
        if (posBary is null || posBary.Length < 18) return;   // fewer than one triangle
        if (!EnsureGrid()) return;

        int verts = posBary.Length / 6;
        int bytes = verts * 5 * sizeof(float);     // pos3 + bary2 is all the layout reads
        if (_gridVbCapacity < bytes || _gridVb.Handle is null)
        {
            _gridVb.Dispose();
            var desc = new BufferDesc
            {
                ByteWidth = (uint)bytes, Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.VertexBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> vb = default;
            if (_device.CreateBuffer(in desc, null, ref vb) < 0) { Log("grid vertex buffer failed"); return; }
            _gridVb = vb; _gridVbCapacity = bytes;
        }

        // Repack 6 floats/vertex down to 5: the third barycentric is derived in the shader, so shipping it
        // would be a third of this buffer wasted on a value that is 1 - x - y.
        var packed = new float[verts * 5];
        for (int v = 0; v < verts; v++)
        {
            packed[v * 5 + 0] = posBary[v * 6 + 0];
            packed[v * 5 + 1] = posBary[v * 6 + 1];
            packed[v * 5 + 2] = posBary[v * 6 + 2];
            packed[v * 5 + 3] = posBary[v * 6 + 3];
            packed[v * 5 + 4] = posBary[v * 6 + 4];
        }

        var map = new MappedSubresource();
        if (_ctx.Map(_gridVb, 0, Map.WriteDiscard, 0, ref map) < 0) return;
        fixed (float* p = packed)
            System.Buffer.MemoryCopy(p, map.PData, (long)bytes, (long)bytes);
        _ctx.Unmap(_gridVb, 0);
        _gridVertexCount = verts;
    }

    public int BucketGridVertexCount => _gridVertexCount;

    /// <summary>M569: navgrid flag cells, with one colour range per visible layer.</summary>
    public void SetNavGridCells(float[]? posBary, (int Start, int Count, Vector4 Color)[]? layers = null)
    {
        _navLayers = layers ?? Array.Empty<(int, int, Vector4)>();
        _navVertexCount = UploadOverlaySoup(posBary, ref _navVb, ref _navVbCapacity, "navgrid");
    }

    /// <summary>M569: the faces currently selected for editing.</summary>
    public void SetSelectedFaces(float[]? posBary) =>
        _faceVertexCount = UploadOverlaySoup(posBary, ref _faceVb, ref _faceVbCapacity, "selected faces");

    /// <summary>
    /// Shared upload for the pos3+bary3 overlay soups. Repacks to the 5 floats the grid layout reads -
    /// the third barycentric is 1 - x - y and the shader derives it.
    /// </summary>
    private int UploadOverlaySoup(float[]? posBary, ref ComPtr<ID3D11Buffer> vb, ref int capacity, string what)
    {
        if (posBary is null || posBary.Length < 18) return 0;
        if (!EnsureGrid()) return 0;

        int verts = posBary.Length / 6;
        int bytes = verts * 5 * sizeof(float);
        if (capacity < bytes || vb.Handle is null)
        {
            vb.Dispose();
            var desc = new BufferDesc
            {
                ByteWidth = (uint)bytes, Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.VertexBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> created = default;
            if (_device.CreateBuffer(in desc, null, ref created) < 0)
            { Log($"{what} vertex buffer failed"); return 0; }
            vb = created; capacity = bytes;
        }

        var packed = new float[verts * 5];
        for (int v = 0; v < verts; v++)
        {
            packed[v * 5 + 0] = posBary[v * 6 + 0];
            packed[v * 5 + 1] = posBary[v * 6 + 1];
            packed[v * 5 + 2] = posBary[v * 6 + 2];
            packed[v * 5 + 3] = posBary[v * 6 + 3];
            packed[v * 5 + 4] = posBary[v * 6 + 4];
        }

        var map = new MappedSubresource();
        if (_ctx.Map(vb, 0, Map.WriteDiscard, 0, ref map) < 0) return 0;
        fixed (float* p = packed)
            System.Buffer.MemoryCopy(p, map.PData, (long)bytes, (long)bytes);
        _ctx.Unmap(vb, 0);
        return verts;
    }

    /// <summary>
    /// M569: the navgrid layers, and then the face selection over them.
    ///
    /// <para>Both draw with depth testing OFF, matching the GL side. These are diagnostics: a bush cell
    /// buried in terrain or a face picked behind a hill still has to be visible, and an overlay you cannot
    /// see is indistinguishable from one that is broken.</para>
    /// </summary>
    private int DrawNavGridAndFaces(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_navVertexCount == 0 && _faceVertexCount == 0) return 0;
        if (!EnsureGrid() || !EnsureOverlay()) return 0;

        var mvp = Matrix4x4.Multiply(view, proj);
        _ctx.IASetInputLayout(_gridLayout);
        _ctx.VSSetShader(_gridVs, null, 0);
        _ctx.PSSetShader(_gridPs, null, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        _ctx.VSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.OMSetBlendState(_overlayBlend, stackalloc float[] { 0f, 0f, 0f, 0f }, 0xFFFFFFFF);
        _ctx.OMSetDepthStencilState(_overlayDepthNoTest, 0);

        uint stride = 5 * sizeof(float), offset = 0;
        int draws = 0;

        if (_navVertexCount > 0 && _navVb.Handle is not null)
        {
            _ctx.IASetVertexBuffers(0, 1, ref _navVb, in stride, in offset);
            if (_navLayers.Length == 0)
            {
                SetOverlayCb(mvp, new Vector4(0.26f, 0.85f, 0.36f, 0.80f));
                _ctx.Draw((uint)_navVertexCount, 0);
                draws++;
            }
            else
                foreach (var (start, count, colour) in _navLayers)
                {
                    if (count <= 0 || start < 0 || start + count > _navVertexCount) continue;
                    SetOverlayCb(mvp, colour);
                    _ctx.Draw((uint)count, (uint)start);
                    draws++;
                }
        }

        if (_faceVertexCount > 0 && _faceVb.Handle is not null)
        {
            SetOverlayCb(mvp, new Vector4(1.00f, 0.62f, 0.16f, 0.85f));
            _ctx.IASetVertexBuffers(0, 1, ref _faceVb, in stride, in offset);
            _ctx.Draw((uint)_faceVertexCount, 0);
            draws++;
        }
        return draws;
    }

    // M296: the transform gizmo. Position-only line segments per axis, so the overlay pipeline draws it
    // as-is; only the topology differs from the rest of the furniture.
    /// <summary>
    /// M659: draw the placement icons THROUGH geometry. Off by default - markers used to be drawn with
    /// no depth test at all, so a particle behind a wall showed anyway and a busy map read as a cloud of
    /// icons belonging to nothing visible. The GIZMO is not covered by this and stays on top always.
    /// </summary>
    public bool IconsThroughWalls { get; set; }

    private ComPtr<ID3D11Buffer> _lightRangeVb;
    private int _lightRangeVbCapacity, _lightRangeVerts;

    private ComPtr<ID3D11Buffer> _gizmoVb;
    /// <summary>M658: culling off, for solid gizmo handles under a winding-reversing mirror.</summary>
    private ComPtr<ID3D11RasterizerState> _gizmoRaster;
    private int _gizmoVbCapacity;
    private readonly int[] _gizmoAxisVerts = new int[3];

    // M361: the paint brush ring, drawn through the same overlay pipeline as the gizmo.
    private ComPtr<ID3D11Buffer> _brushRingVb;
    private int _brushRingVbCapacity;
    private int _brushRingVerts;
    private ComPtr<ID3D11Buffer> _bakeBoxVb;   // M412: bake-volume preview lines
    private int _bakeBoxVbCapacity;
    private int _bakeBoxVerts;
    // M619: the skeleton overlay. Its own small VB for the same reason the bake box has one - it is
    // rewritten EVERY FRAME while an animation plays, and sharing a channel with anything larger would
    // re-upload that too, sixty times a second.
    private ComPtr<ID3D11Buffer> _boneVb;
    private int _boneVbCapacity;
    private int _boneVerts;
    // M628: the target dummy's box, for the case where its real model is unavailable.
    private ComPtr<ID3D11Buffer> _dummyVb;
    private int _dummyVbCapacity;
    // M639: the cast-range ring, its own buffer on the same overlay pipeline
    private ComPtr<ID3D11Buffer> _rangeVb;
    private int _rangeVbCapacity;
    private int _rangeVerts;
    private int _dummyVerts;
    private int _gizmoTotalVerts;

    /// <summary>
    /// <para>M296: the gizmo's three axis arms, already built by
    /// <c>ViewportMeshRenderer.BuildGizmoAxis</c> - the same builder the GL viewport uses, so both draw
    /// the arm the hit-test measures against.</para>
    ///
    /// <para>Only DRAWING was missing on D3D11. Dragging already worked: the transparent input border over
    /// the viewport swallows pointer events in both modes and the hit-test is CPU maths against matrices
    /// SyncPickMatrices refreshes every D3D11 frame. The user simply had nothing to see or aim at.</para>
    /// </summary>
    public void SetGizmoGeometry(float[]? x, float[]? y, float[]? z)
    {
        _gizmoTotalVerts = 0;
        _gizmoAxisVerts[0] = _gizmoAxisVerts[1] = _gizmoAxisVerts[2] = 0;
        int floats = (x?.Length ?? 0) + (y?.Length ?? 0) + (z?.Length ?? 0);
        if (floats < 6 || !EnsureOverlay()) return;

        int bytes = floats * sizeof(float);
        if (_gizmoVbCapacity < bytes || _gizmoVb.Handle is null)
        {
            _gizmoVb.Dispose();
            var desc = new BufferDesc
            {
                ByteWidth = (uint)bytes, Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.VertexBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> vb = default;
            if (_device.CreateBuffer(in desc, null, ref vb) < 0) { Log("gizmo vertex buffer failed"); return; }
            _gizmoVb = vb; _gizmoVbCapacity = bytes;
        }

        var all = new float[floats];
        int at = 0;
        void Append(float[]? a, int slot)
        {
            if (a is null || a.Length < 6) return;
            Array.Copy(a, 0, all, at, a.Length);
            at += a.Length;
            _gizmoAxisVerts[slot] = a.Length / 3;
        }
        Append(x, 0); Append(y, 1); Append(z, 2);

        var map = new MappedSubresource();
        if (_ctx.Map(_gizmoVb, 0, Map.WriteDiscard, 0, ref map) < 0) return;
        fixed (float* p = all)
            System.Buffer.MemoryCopy(p, map.PData, (long)bytes, (long)bytes);
        _ctx.Unmap(_gizmoVb, 0);
        _gizmoTotalVerts = floats / 3;
    }

    /// <summary>M361: the brush ring as a world-space line list, built by
    /// <c>ViewportMeshRenderer.BuildBrushRing</c> so both viewports draw the identical ring. Null clears it.</summary>
    public void SetBrushRingLines(float[]? verts)
    {
        _brushRingVerts = 0;
        if (verts is null || verts.Length < 6 || !EnsureOverlay()) return;

        int bytes = verts.Length * sizeof(float);
        if (_brushRingVbCapacity < bytes || _brushRingVb.Handle is null)
        {
            _brushRingVb.Dispose();
            var desc = new BufferDesc
            {
                ByteWidth = (uint)bytes, Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.VertexBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> vb = default;
            if (_device.CreateBuffer(in desc, null, ref vb) < 0) { Log("brush ring vertex buffer failed"); return; }
            _brushRingVb = vb; _brushRingVbCapacity = bytes;
        }

        var map = new MappedSubresource();
        if (_ctx.Map(_brushRingVb, 0, Map.WriteDiscard, 0, ref map) < 0) return;
        fixed (float* p = verts)
            System.Buffer.MemoryCopy(p, map.PData, (long)bytes, (long)bytes);
        _ctx.Unmap(_brushRingVb, 0);
        _brushRingVerts = verts.Length / 3;
    }

    /// <summary>M619: the animated skeleton, as the same position pairs the GL viewport draws
    /// (<c>SkinnedFrame.BoneSegments</c>). Null clears it.</summary>
    public void SetBoneLines(float[]? verts)
    {
        _boneVerts = 0;
        if (verts is null || verts.Length < 6 || !EnsureOverlay()) return;

        int bytes = verts.Length * sizeof(float);
        if (_boneVbCapacity < bytes || _boneVb.Handle is null)
        {
            _boneVb.Dispose();
            var desc = new BufferDesc
            {
                ByteWidth = (uint)bytes, Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.VertexBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> vb = default;
            if (_device.CreateBuffer(in desc, null, ref vb) < 0) { Log("bone vertex buffer failed"); return; }
            _boneVb = vb; _boneVbCapacity = bytes;
        }

        var map = new MappedSubresource();
        if (_ctx.Map(_boneVb, 0, Map.WriteDiscard, 0, ref map) < 0) return;
        fixed (float* p = verts)
            System.Buffer.MemoryCopy(p, map.PData, (long)bytes, (long)bytes);
        _ctx.Unmap(_boneVb, 0);
        _boneVerts = verts.Length / 3;
    }

    /// <summary>M628: the target dummy as a wire box, for hosts with no dummy MODEL to place. GL draws a
    /// solid-plus-wireframe cube; this is the wireframe half, built from the same
    /// <c>ViewportMeshRenderer.BuildBoxLines</c> so the two viewports agree on where it is.</summary>
    public void SetDummyLines(float[]? verts)
    {
        _dummyVerts = 0;
        if (verts is null || verts.Length < 6 || !EnsureOverlay()) return;

        int bytes = verts.Length * sizeof(float);
        if (_dummyVbCapacity < bytes || _dummyVb.Handle is null)
        {
            _dummyVb.Dispose();
            var desc = new BufferDesc
            {
                ByteWidth = (uint)bytes, Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.VertexBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> vb = default;
            if (_device.CreateBuffer(in desc, null, ref vb) < 0) { Log("dummy vertex buffer failed"); return; }
            _dummyVb = vb; _dummyVbCapacity = bytes;
        }

        var map = new MappedSubresource();
        if (_ctx.Map(_dummyVb, 0, Map.WriteDiscard, 0, ref map) < 0) return;
        fixed (float* p = verts)
            System.Buffer.MemoryCopy(p, map.PData, (long)bytes, (long)bytes);
        _ctx.Unmap(_dummyVb, 0);
        _dummyVerts = verts.Length / 3;
    }

    private int DrawDummyLines(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_dummyVerts == 0 || _dummyVb.Handle is null || !EnsureOverlay()) return 0;

        var mvp = Matrix4x4.Multiply(view, proj);
        _ctx.IASetInputLayout(_overlayLayout);
        _ctx.VSSetShader(_overlayVs, null, 0);
        _ctx.PSSetShader(_overlayPs, null, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist);

        uint stride = 3 * sizeof(float), offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _dummyVb, in stride, in offset);
        _ctx.OMSetBlendState(_overlayBlend, stackalloc float[] { 0f, 0f, 0f, 0f }, 0xFFFFFFFF);
        // Depth-tested, unlike the bones: this stands in for a solid object and should be occluded by one.
        _ctx.OMSetDepthStencilState(_overlayDepth, 0);

        SetOverlayCb(mvp, new Vector4(0.90f, 0.30f, 0.30f, 1f));
        _ctx.VSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.Draw((uint)_dummyVerts, 0);

        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        return 1;
    }

    /// <summary>M639: the cast-range ring as a line list (xyz pairs). Null or short clears it.</summary>
    public void SetRangeLines(float[]? verts)
    {
        _rangeVerts = 0;
        if (verts is null || verts.Length < 6 || !EnsureOverlay()) return;

        int bytes = verts.Length * sizeof(float);
        if (_rangeVbCapacity < bytes || _rangeVb.Handle is null)
        {
            _rangeVb.Dispose();
            var desc = new BufferDesc
            {
                ByteWidth = (uint)bytes, Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.VertexBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> vb = default;
            if (_device.CreateBuffer(in desc, null, ref vb) < 0) { Log("range ring vertex buffer failed"); return; }
            _rangeVb = vb; _rangeVbCapacity = bytes;
        }

        var map = new MappedSubresource();
        if (_ctx.Map(_rangeVb, 0, Map.WriteDiscard, 0, ref map) < 0) return;
        fixed (float* p = verts)
            System.Buffer.MemoryCopy(p, map.PData, (long)bytes, (long)bytes);
        _ctx.Unmap(_rangeVb, 0);
        _rangeVerts = verts.Length / 3;
    }

    private int DrawRangeLines(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_rangeVerts == 0 || _rangeVb.Handle is null || !EnsureOverlay()) return 0;

        var mvp = Matrix4x4.Multiply(view, proj);
        _ctx.IASetInputLayout(_overlayLayout);
        _ctx.VSSetShader(_overlayVs, null, 0);
        _ctx.PSSetShader(_overlayPs, null, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist);

        uint stride = 3 * sizeof(float), offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _rangeVb, in stride, in offset);
        _ctx.OMSetBlendState(_overlayBlend, stackalloc float[] { 0f, 0f, 0f, 0f }, 0xFFFFFFFF);
        // Depth-tested like the dummy: the ring lies on the ground and terrain in front of the camera
        // should hide the part behind it.
        _ctx.OMSetDepthStencilState(_overlayDepth, 0);

        SetOverlayCb(mvp, new Vector4(0.40f, 0.95f, 0.55f, 1f));
        _ctx.VSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.Draw((uint)_rangeVerts, 0);

        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        return 1;
    }

    private int DrawBoneLines(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_boneVerts == 0 || _boneVb.Handle is null || !EnsureOverlay()) return 0;

        var mvp = Matrix4x4.Multiply(view, proj);
        _ctx.IASetInputLayout(_overlayLayout);
        _ctx.VSSetShader(_overlayVs, null, 0);
        _ctx.PSSetShader(_overlayPs, null, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist);

        uint stride = 3 * sizeof(float), offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _boneVb, in stride, in offset);
        _ctx.OMSetBlendState(_overlayBlend, stackalloc float[] { 0f, 0f, 0f, 0f }, 0xFFFFFFFF);
        // No depth test, exactly as GL draws it: a skeleton you can only see where it pokes out of the
        // mesh is not a skeleton overlay.
        _ctx.OMSetDepthStencilState(_overlayDepthNoTest, 0);

        // GL's (1.0, 0.65, 0.2). The two viewports must draw the same bones the same colour or an A/B
        // between them turns into a discussion about the colour.
        SetOverlayCb(mvp, new Vector4(1.0f, 0.65f, 0.2f, 1f));
        _ctx.VSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.Draw((uint)_boneVerts, 0);

        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        return 1;
    }

    /// <summary>M412: the bucket-grid bake volume, built by ViewportMeshRenderer.BuildBoxLines so both
    /// viewports draw the identical box. Its own small VB - it must never ride on the multi-megabyte
    /// bucket-grid channel, where publishing it would re-upload the whole grid. Null clears it.</summary>
    public void SetBakeBoxLines(float[]? verts)
    {
        _bakeBoxVerts = 0;
        if (verts is null || verts.Length < 6 || !EnsureOverlay()) return;

        int bytes = verts.Length * sizeof(float);
        if (_bakeBoxVbCapacity < bytes || _bakeBoxVb.Handle is null)
        {
            _bakeBoxVb.Dispose();
            var desc = new BufferDesc
            {
                ByteWidth = (uint)bytes, Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.VertexBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> vb = default;
            if (_device.CreateBuffer(in desc, null, ref vb) < 0) { Log("bake box vertex buffer failed"); return; }
            _bakeBoxVb = vb; _bakeBoxVbCapacity = bytes;
        }

        var map = new MappedSubresource();
        if (_ctx.Map(_bakeBoxVb, 0, Map.WriteDiscard, 0, ref map) < 0) return;
        fixed (float* p = verts)
            System.Buffer.MemoryCopy(p, map.PData, (long)bytes, (long)bytes);
        _ctx.Unmap(_bakeBoxVb, 0);
        _bakeBoxVerts = verts.Length / 3;
    }

    private int DrawBakeBox(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_bakeBoxVerts == 0 || _bakeBoxVb.Handle is null || !EnsureOverlay()) return 0;

        var mvp = Matrix4x4.Multiply(view, proj);
        _ctx.IASetInputLayout(_overlayLayout);
        _ctx.VSSetShader(_overlayVs, null, 0);
        _ctx.PSSetShader(_overlayPs, null, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist);

        uint stride = 3 * sizeof(float), offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _bakeBoxVb, in stride, in offset);
        _ctx.OMSetBlendState(_overlayBlend, stackalloc float[] { 0f, 0f, 0f, 0f }, 0xFFFFFFFF);
        // Always on top, like the GL side draws it - a preview volume hidden inside terrain is useless.
        _ctx.OMSetDepthStencilState(_overlayDepthNoTest, 0);

        // Violet, matching GL's (0.80, 0.40, 0.95) exactly - the two viewports must show the same box.
        SetOverlayCb(mvp, new Vector4(0.80f, 0.40f, 0.95f, 1f));
        _ctx.VSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.Draw((uint)_bakeBoxVerts, 0);

        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);
        return 1;
    }

    private int DrawBrushRing(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_brushRingVerts == 0 || _brushRingVb.Handle is null || !EnsureOverlay()) return 0;

        var mvp = Matrix4x4.Multiply(view, proj);
        _ctx.IASetInputLayout(_overlayLayout);
        _ctx.VSSetShader(_overlayVs, null, 0);
        _ctx.PSSetShader(_overlayPs, null, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist);

        uint stride = 3 * sizeof(float), offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _brushRingVb, in stride, in offset);
        _ctx.OMSetBlendState(_overlayBlend, stackalloc float[] { 0f, 0f, 0f, 0f }, 0xFFFFFFFF);

        // Depth test OFF, like the gizmo. The ring hugs the surface it is about to paint, so with depth
        // testing on it z-fights the very geometry it is meant to sit on - GL lifts it along the normal
        // for the same reason and still draws it last.
        _ctx.OMSetDepthStencilState(_overlayDepthNoTest, 0);

        SetOverlayCb(mvp, new Vector4(1f, 0.85f, 0.25f, 1f));
        _ctx.VSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.Draw((uint)_brushRingVerts, 0);

        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        return 1;
    }

    /// <summary>M659: line segments for the point-light radius rings, in world space.</summary>
    public void SetLightRangeLines(float[]? verts)
    {
        _lightRangeVerts = 0;
        if (verts is null || verts.Length < 6 || !EnsureOverlay()) return;

        int bytes = verts.Length * sizeof(float);
        if (_lightRangeVbCapacity < bytes || _lightRangeVb.Handle is null)
        {
            _lightRangeVb.Dispose();
            var desc = new BufferDesc
            {
                ByteWidth = (uint)bytes, Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.VertexBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> vb = default;
            if (_device.CreateBuffer(in desc, null, ref vb) < 0) { Log("light range buffer failed"); return; }
            _lightRangeVb = vb; _lightRangeVbCapacity = bytes;
        }

        var map = new MappedSubresource();
        if (_ctx.Map(_lightRangeVb, 0, Map.WriteDiscard, 0, ref map) < 0) return;
        unsafe { fixed (float* src = verts) System.Buffer.MemoryCopy(src, map.PData, bytes, bytes); }
        _ctx.Unmap(_lightRangeVb, 0);
        _lightRangeVerts = verts.Length / 3;
    }

    /// <summary>M659: how far each dynamic point light reaches. Same visibility rule as the icons - the
    /// ring belongs to the light marker, and one hidden by the wall it stops at is the useful picture.</summary>
    private int DrawLightRanges(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_lightRangeVerts == 0 || _lightRangeVb.Handle is null || !EnsureOverlay()) return 0;

        var mvp = Matrix4x4.Multiply(view, proj);
        _ctx.IASetInputLayout(_overlayLayout);
        _ctx.VSSetShader(_overlayVs, null, 0);
        _ctx.PSSetShader(_overlayPs, null, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist);

        uint stride = 3 * sizeof(float), offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _lightRangeVb, in stride, in offset);
        _ctx.OMSetBlendState(_overlayBlend, stackalloc float[] { 0f, 0f, 0f, 0f }, 0xFFFFFFFF);
        _ctx.OMSetDepthStencilState(IconsThroughWalls ? _overlayDepthNoTest : _overlayDepth, 0);

        SetOverlayCb(mvp, new Vector4(1.0f, 0.83f, 0.35f, 0.75f));   // the light icon's own warm yellow
        _ctx.VSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.Draw((uint)_lightRangeVerts, 0);

        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        return 1;
    }

    private int DrawGizmo(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_gizmoTotalVerts == 0 || _gizmoVb.Handle is null || !EnsureOverlay()) return 0;

        var mvp = Matrix4x4.Multiply(view, proj);
        _ctx.IASetInputLayout(_overlayLayout);
        _ctx.VSSetShader(_overlayVs, null, 0);
        _ctx.PSSetShader(_overlayPs, null, 0);
        // M658: the handles are solid geometry now, not line segments.
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3D11PrimitiveTopologyTrianglelist);

        uint stride = 3 * sizeof(float), offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _gizmoVb, in stride, in offset);

        // Depth test OFF, exactly as GL draws it: a gizmo occluded by the thing it moves is a gizmo you
        // cannot grab, so it is always on top. Culling off for the same reason GL turns it off: a mirrored
        // view reverses winding, and a gizmo that depended on facing would vanish in one viewport only.
        _ctx.OMSetBlendState(_overlayBlend, stackalloc float[] { 0f, 0f, 0f, 0f }, 0xFFFFFFFF);
        _ctx.OMSetDepthStencilState(_overlayDepthNoTest, 0);
        if (_gizmoRaster.Handle is not null) _ctx.RSSetState(_gizmoRaster);

        // The same three colours the GL viewport uses - X red, Y green, Z blue.
        var colours = stackalloc Vector4[3];
        colours[0] = new Vector4(0.95f, 0.25f, 0.25f, 1f);
        colours[1] = new Vector4(0.30f, 0.90f, 0.35f, 1f);
        colours[2] = new Vector4(0.30f, 0.55f, 0.98f, 1f);

        int drawn = 0, first = 0;
        for (int a = 0; a < 3; a++)
        {
            int n = _gizmoAxisVerts[a];
            if (n <= 0) continue;
            SetOverlayCb(mvp, colours[a]);
            _ctx.VSSetConstantBuffers(0, 1, ref _overlayCb);
            _ctx.PSSetConstantBuffers(0, 1, ref _overlayCb);
            _ctx.Draw((uint)n, (uint)first);
            first += n;
            drawn++;
        }

        // Topology is already triangles (M658). The RASTERIZER is device state too and was swapped for a
        // no-cull one, so put the frame's own back - otherwise the next pass is silently two-sided.
        if (_gizmoRaster.Handle is not null && _raster.Handle is not null) _ctx.RSSetState(_raster);
        return drawn;
    }

    private int DrawBucketGrid(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_gridVertexCount == 0 || _gridVb.Handle is null || !EnsureGrid()) return 0;
        if (!EnsureOverlay()) return 0;   // shares the overlay's cbuffer, blend and depth states

        SetOverlayCb(Matrix4x4.Multiply(view, proj), new Vector4(0.62f, 0.45f, 0.95f, 0.85f));

        _ctx.IASetInputLayout(_gridLayout);
        _ctx.VSSetShader(_gridVs, null, 0);
        _ctx.PSSetShader(_gridPs, null, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        _ctx.VSSetConstantBuffers(0, 1, ref _overlayCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _overlayCb);

        uint stride = 5 * sizeof(float), offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref _gridVb, in stride, in offset);

        // Depth TESTED but not written, and alpha blended - the same state GL draws it under, so the grid
        // is occluded by geometry in front of it without disturbing anything drawn after.
        _ctx.OMSetBlendState(_overlayBlend, stackalloc float[] { 0f, 0f, 0f, 0f }, 0xFFFFFFFF);
        _ctx.OMSetDepthStencilState(_overlayDepth, 0);
        _ctx.Draw((uint)_gridVertexCount, 0);
        return 1;
    }

    private bool EnsureMesh()
    {
        if (_meshTried) return _meshVs.Handle is not null;
        _meshTried = true;

        ID3D10Blob* vsCode = null, psCode = null, errs = null;
        var src = System.Text.Encoding.ASCII.GetBytes(MeshHlsl);
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* sp = src)
            {
                var vsEntry = System.Text.Encoding.ASCII.GetBytes("vsmain\0");
                var vsTarget = System.Text.Encoding.ASCII.GetBytes("vs_5_0\0");
                fixed (byte* ep = vsEntry) fixed (byte* tp = vsTarget)
                    if (compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                            ep, tp, 0u, 0u, &vsCode, &errs) < 0 || vsCode is null)
                    { Log("mesh vs failed to compile"); return false; }

                var psEntry = System.Text.Encoding.ASCII.GetBytes("psmain\0");
                var psTarget = System.Text.Encoding.ASCII.GetBytes("ps_5_0\0");
                fixed (byte* ep = psEntry) fixed (byte* tp = psTarget)
                    if (compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                            ep, tp, 0u, 0u, &psCode, &errs) < 0 || psCode is null)
                    { Log("mesh ps failed to compile"); return false; }
            }
        }
        catch (Exception ex) { Log("mesh: the HLSL compiler is unavailable: " + ex.Message); return false; }

        ComPtr<ID3D11VertexShader> vs = default;
        if (_device.CreateVertexShader(vsCode->GetBufferPointer(), vsCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vs) < 0)
        { Log("mesh CreateVertexShader failed"); return false; }
        _meshVs = vs;

        ComPtr<ID3D11PixelShader> ps = default;
        if (_device.CreatePixelShader(psCode->GetBufferPointer(), psCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref ps) < 0)
        { Log("mesh CreatePixelShader failed"); return false; }
        _meshPs = ps;

        var semPos = System.Text.Encoding.ASCII.GetBytes("POSITION\0");
        var semUv = System.Text.Encoding.ASCII.GetBytes("TEXCOORD\0");
        fixed (byte* sp0 = semPos)
        fixed (byte* sp1 = semUv)
        {
            var els = stackalloc InputElementDesc[2];
            els[0] = new InputElementDesc
            {
                SemanticName = sp0, SemanticIndex = 0, Format = Format.FormatR32G32B32Float,
                InputSlot = 0, AlignedByteOffset = 0,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            els[1] = new InputElementDesc
            {
                SemanticName = sp1, SemanticIndex = 0, Format = Format.FormatR32G32Float,
                InputSlot = 0, AlignedByteOffset = 12,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            ComPtr<ID3D11InputLayout> layout = default;
            if (_device.CreateInputLayout(els, 2, vsCode->GetBufferPointer(), vsCode->GetBufferSize(), ref layout) < 0)
            { Log("mesh CreateInputLayout failed"); return false; }
            _meshLayout = layout;
        }

        var cbDesc = new BufferDesc
        {
            ByteWidth = 256,          // M295: +float4x4 gModel
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> cb = default;
        if (_device.CreateBuffer(in cbDesc, null, ref cb) < 0) { Log("mesh cbuffer failed"); return false; }
        _meshCb = cb;

        // Two states for one convention. GL culls with front = CW, and flips to CCW when a particle's
        // scale is negative - a uniform negative scale has a negative determinant, so it reverses winding
        // and the correct faces would otherwise be the ones discarded (VfxParticleRenderer.cs:1006-1008).
        foreach (bool ccw in new[] { false, true })
        {
            var rd = new RasterizerDesc
            {
                FillMode = FillMode.Solid, CullMode = CullMode.Back,
                FrontCounterClockwise = ccw ? (Silk.NET.Core.Bool32)true : false,
                DepthClipEnable = true,
            };
            ComPtr<ID3D11RasterizerState> rs = default;
            _device.CreateRasterizerState(in rd, ref rs);
            if (ccw) _meshCullCcw = rs; else _meshCullCw = rs;
        }

        Log("mesh particle pipeline built");
        return true;
    }

    /// <summary>Upload one emitter's mesh and return a handle. Positions and UVs are interleaved here
    /// rather than kept as parallel arrays, because the re-skin path rewrites only the position floats in
    /// place and re-uploads the whole buffer - which is what the GL side does too.</summary>
    public int CreateMeshGeometry(float[] positions, float[] uvs, uint[]? indices)
    {
        if (!EnsureMesh()) return -1;
        int vertexCount = positions.Length / 3;
        if (vertexCount == 0) return -1;

        var interleaved = new float[vertexCount * MeshVertexStride];
        for (int v = 0; v < vertexCount; v++)
        {
            interleaved[v * MeshVertexStride + 0] = positions[v * 3 + 0];
            interleaved[v * MeshVertexStride + 1] = positions[v * 3 + 1];
            interleaved[v * MeshVertexStride + 2] = positions[v * 3 + 2];
            interleaved[v * MeshVertexStride + 3] = v * 2 + 1 < uvs.Length ? uvs[v * 2 + 0] : 0f;
            interleaved[v * MeshVertexStride + 4] = v * 2 + 1 < uvs.Length ? uvs[v * 2 + 1] : 0f;
        }

        var geom = new MeshGeom { VertexCount = vertexCount, Interleaved = interleaved };

        var vbDesc = new BufferDesc
        {
            ByteWidth = (uint)(interleaved.Length * sizeof(float)),
            Usage = Usage.Dynamic, BindFlags = (uint)BindFlag.VertexBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> vb = default;
        if (_device.CreateBuffer(in vbDesc, null, ref vb) < 0) { Log("mesh vertex buffer failed"); return -1; }
        geom.Vb = vb;
        UploadMeshVertices(geom);

        if (indices is { Length: > 0 })
        {
            var ibDesc = new BufferDesc
            {
                ByteWidth = (uint)(indices.Length * sizeof(uint)),
                Usage = Usage.Default, BindFlags = (uint)BindFlag.IndexBuffer,
            };
            ComPtr<ID3D11Buffer> ib = default;
            fixed (uint* p = indices)
            {
                var sub = new SubresourceData { PSysMem = p };
                if (_device.CreateBuffer(in ibDesc, in sub, ref ib) >= 0) { geom.Ib = ib; geom.IndexCount = indices.Length; }
                else Log("mesh index buffer failed");
            }
        }

        int free = _meshGeoms.FindIndex(g => g is null);
        if (free >= 0) { _meshGeoms[free] = geom; return free; }
        _meshGeoms.Add(geom);
        return _meshGeoms.Count - 1;
    }

    /// <summary>Rewrite an animated emitter's positions from a freshly skinned frame. UVs are untouched -
    /// skinning moves vertices, it does not re-parameterise the surface.</summary>
    public void UpdateMeshGeometryPositions(int id, float[] positions)
    {
        if (id < 0 || id >= _meshGeoms.Count) return;
        var geom = _meshGeoms[id];
        if (geom is null) return;
        int n = Math.Min(geom.VertexCount, positions.Length / 3);
        for (int v = 0; v < n; v++)
        {
            geom.Interleaved[v * MeshVertexStride + 0] = positions[v * 3 + 0];
            geom.Interleaved[v * MeshVertexStride + 1] = positions[v * 3 + 1];
            geom.Interleaved[v * MeshVertexStride + 2] = positions[v * 3 + 2];
        }
        UploadMeshVertices(geom);
    }

    private void UploadMeshVertices(MeshGeom geom)
    {
        if (geom.Vb.Handle is null) return;
        var map = new MappedSubresource();
        if (_ctx.Map(geom.Vb, 0, Map.WriteDiscard, 0, ref map) < 0) return;
        fixed (float* src = geom.Interleaved)
            System.Buffer.MemoryCopy(src, map.PData, (long)geom.Interleaved.Length * sizeof(float),
                (long)geom.Interleaved.Length * sizeof(float));
        _ctx.Unmap(geom.Vb, 0);
    }

    public int MeshGeometryCount => _meshGeoms.Count(g => g is not null);

    public void ReleaseMeshGeometry(int id)
    {
        if (id < 0 || id >= _meshGeoms.Count || _meshGeoms[id] is not { } geom) return;
        geom.Vb.Dispose();
        geom.Ib.Dispose();
        _meshGeoms[id] = null;
    }

    private void ReleaseMeshGeometry()
    {
        foreach (var g in _meshGeoms)
            if (g is not null) { g.Vb.Dispose(); g.Ib.Dispose(); }
        _meshGeoms.Clear();
        ReleaseRiotMeshGeometry();
    }

    // ---- M640: mesh-primitive geometry for RIOT'S mesh shaders ------------------------------------

    private sealed class RiotMeshGeom
    {
        public ComPtr<ID3D11Buffer> Vb, Ib;
        public int VertexCount, IndexCount;
    }
    private readonly List<RiotMeshGeom?> _riotMeshGeoms = new();

    /// <summary>M676: the placement being drawn while a <see cref="PreviewMaterial.CharacterInstances"/>
    /// material is mid-loop; the bones and mWorld fills read it ahead of the frame's World. Null outside
    /// that loop, which is every other draw.</summary>
    private Matrix4x4? _instanceWorld;

    /// <summary>
    /// Upload a mesh primitive in the layout Riot's <c>particlesystem/mesh_vs</c> declares - POSITION0,
    /// NORMAL0, TEXCOORD0 - which is <see cref="PreviewVertex"/>, the same vertex the material path builds
    /// its input layouts against. Returns an id for <see cref="PreviewMaterial.RiotMeshGeometryId"/>.
    ///
    /// <para>Normals are REQUIRED by that shader even when nothing reads the result: it normalises the
    /// world-space normal with <c>rsq</c> for the fresnel term, and a zero normal makes that NaN, which
    /// the pixel stage then adds into the colour. .scb/.sco primitives carry no normals, so when
    /// <paramref name="normals"/> is null they are computed here from the triangles, area-weighted.</para>
    /// </summary>
    public int CreateRiotMeshGeometry(float[] positions, float[]? normals, float[] uvs, uint[]? indices)
    {
        int vertexCount = positions.Length / 3;
        if (vertexCount == 0) return -1;
        var idx = indices is { Length: > 0 } ? indices : Enumerable.Range(0, vertexCount).Select(i => (uint)i).ToArray();
        if (idx.Length < 3) return -1;

        float[] n = normals is { Length: > 0 } && normals.Length == positions.Length ? normals : FlatNormals(positions, idx);
        var verts = new PreviewVertex[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            float u = v * 2 + 1 < uvs.Length ? uvs[v * 2] : 0f;
            float w = v * 2 + 1 < uvs.Length ? uvs[v * 2 + 1] : 0f;
            verts[v] = new PreviewVertex
            {
                Position = new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]),
                Normal = new Vector3(n[v * 3], n[v * 3 + 1], n[v * 3 + 2]),
                Uv0 = new Vector4(u, w, 0f, 0f),
                Uv1 = new Vector2(u, w),
                Color = Vector4.One,
            };
        }

        return RegisterRiotGeometry(verts, idx);
    }

    /// <summary>M676: a SKINNED mesh in the same store - the vertices exactly as <see cref="SetMesh"/> takes
    /// them, blend indices and weights included - for a placed prop drawn through Riot's character shaders
    /// with <see cref="PreviewMaterial.CharacterInstances"/>. Immutable: the pose is the bone palette's
    /// job, not a per-frame vertex upload's, which is also why one geometry serves every placement.</summary>
    public int CreateRiotMeshGeometry(PreviewMesh mesh)
    {
        if (mesh.Vertices.Length == 0 || mesh.Indices.Length < 3) return -1;
        return RegisterRiotGeometry(mesh.Vertices, mesh.Indices);
    }

    private int RegisterRiotGeometry(PreviewVertex[] verts, uint[] idx)
    {
        var geom = new RiotMeshGeom { VertexCount = verts.Length, IndexCount = idx.Length };
        var vdesc = new BufferDesc
        {
            ByteWidth = (uint)(verts.Length * PreviewVertex.SizeInBytes),
            Usage = Usage.Immutable, BindFlags = (uint)BindFlag.VertexBuffer,
        };
        fixed (PreviewVertex* p = verts)
        {
            var sub = new SubresourceData { PSysMem = p };
            ComPtr<ID3D11Buffer> b = default;
            if (_device.CreateBuffer(in vdesc, in sub, ref b) < 0) { Log("riot mesh vertex buffer failed"); return -1; }
            geom.Vb = b;
        }
        var idesc = new BufferDesc
        {
            ByteWidth = (uint)(idx.Length * sizeof(uint)),
            Usage = Usage.Immutable, BindFlags = (uint)BindFlag.IndexBuffer,
        };
        fixed (uint* p = idx)
        {
            var sub = new SubresourceData { PSysMem = p };
            ComPtr<ID3D11Buffer> b = default;
            if (_device.CreateBuffer(in idesc, in sub, ref b) < 0) { geom.Vb.Dispose(); Log("riot mesh index buffer failed"); return -1; }
            geom.Ib = b;
        }

        for (int i = 0; i < _riotMeshGeoms.Count; i++)
            if (_riotMeshGeoms[i] is null) { _riotMeshGeoms[i] = geom; return i; }
        _riotMeshGeoms.Add(geom);
        return _riotMeshGeoms.Count - 1;
    }

    public void ReleaseRiotMeshGeometry(int id)
    {
        if (id < 0 || id >= _riotMeshGeoms.Count || _riotMeshGeoms[id] is not { } geom) return;
        geom.Vb.Dispose();
        geom.Ib.Dispose();
        _riotMeshGeoms[id] = null;
    }

    private void ReleaseRiotMeshGeometry()
    {
        foreach (var g in _riotMeshGeoms)
            if (g is not null) { g.Vb.Dispose(); g.Ib.Dispose(); }
        _riotMeshGeoms.Clear();
    }

    private static float[] FlatNormals(float[] positions, uint[] indices)
    {
        var n = new float[positions.Length];
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int a = (int)indices[t], b = (int)indices[t + 1], c = (int)indices[t + 2];
            if ((c + 1) * 3 > positions.Length || (a + 1) * 3 > positions.Length || (b + 1) * 3 > positions.Length) continue;
            var pa = new Vector3(positions[a * 3], positions[a * 3 + 1], positions[a * 3 + 2]);
            var pb = new Vector3(positions[b * 3], positions[b * 3 + 1], positions[b * 3 + 2]);
            var pc = new Vector3(positions[c * 3], positions[c * 3 + 1], positions[c * 3 + 2]);
            var face = Vector3.Cross(pb - pa, pc - pa);   // area-weighted by construction
            foreach (int v in new[] { a, b, c })
            { n[v * 3] += face.X; n[v * 3 + 1] += face.Y; n[v * 3 + 2] += face.Z; }
        }
        for (int v = 0; v * 3 + 2 < n.Length; v++)
        {
            var nv = new Vector3(n[v * 3], n[v * 3 + 1], n[v * 3 + 2]);
            nv = nv.LengthSquared() > 1e-12f ? Vector3.Normalize(nv) : Vector3.UnitY;   // never zero: see the remarks
            n[v * 3] = nv.X; n[v * 3 + 1] = nv.Y; n[v * 3 + 2] = nv.Z;
        }
        return n;
    }

    /// <summary>
    /// M640: draw a Riot-shader mesh emitter, once per particle. The material's pipeline, textures and
    /// samplers are already bound by the loop; this binds the geometry and re-issues the two constants the
    /// shader takes per instance - <c>mWorld</c> (CharacterPerDrawVertexCB) and <c>kColorFactor</c>
    /// ($Globals, the particle colour - mesh_vs has no COLOR input) - through the same by-name constant
    /// filling every other material uses, so a material Param and a per-instance value cannot disagree.
    ///
    /// <para>The world matrix is scale, then the particle's Euler rotation, then the emitter's placement
    /// basis, then the position. The Euler is the instance's own (X = the integrated spin, Y/Z = the
    /// authored birth rotation - the same triple the arbitrary-quad path rotates by), applied X, Y, Z in
    /// the order GL's rotateEuler applies it. Measured over ten champions, 513 of 684 mesh emitters author
    /// a non-zero birth rotation - (0,180,0), (1,90,90), (-90,0,0), (90,0,0) - and both renderers used to
    /// draw every one of them unrotated.</para>
    /// </summary>
    /// <summary>
    /// M676: a placed prop, once per placement. The material's pipeline, textures and samplers are bound by
    /// the loop; this binds the prop's own skinned geometry and then, per placement, refills EVERY constant
    /// buffer of the material with that placement as the world - the bones (skin * placement) and mWorld
    /// read <see cref="_instanceWorld"/> - and draws the material's index range.
    ///
    /// <para>Every buffer, not just the bones: a skinned shader may take mWorld, its inverse or the mesh
    /// centre from a per-draw block, and ResolveCb gives a CharacterInstances material buffers of its own,
    /// so nothing here disturbs another material's. The debug constants (M661) sit at b0 and are re-bound
    /// after the material's own, per placement, for the same reason the loop binds them after.</para>
    /// </summary>
    private void DrawCharacterInstances(PreviewMaterial mat, int geometryId, IReadOnlyList<Matrix4x4> placements,
        PreviewSettings s, Matrix4x4 view, Matrix4x4 proj, List<string>? unbound, bool debugBound, Matrix4x4 frameWorld)
    {
        if (geometryId < 0 || geometryId >= _riotMeshGeoms.Count || _riotMeshGeoms[geometryId] is not { } geom) return;
        uint count = mat.IndexCount < 0 ? (uint)geom.IndexCount : (uint)mat.IndexCount;
        if (count == 0 || placements.Count == 0) return;

        uint stride = PreviewVertex.SizeInBytes, offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref geom.Vb, in stride, in offset);
        _ctx.IASetIndexBuffer(geom.Ib, Format.FormatR32Uint, 0);

        for (int i = 0; i < placements.Count; i++)
        {
            var placement = placements[i];
            _instanceWorld = placement;
            // M680: the ambient cube of THIS placement. Params win over the stand-in in the fill, and the
            // material is its own (ResolveCb), so the per-draw block re-uploads with it every placement.
            if (mat.CharacterInstanceAmbient is { } ambient && i < ambient.Count && ambient[i] is { } cube)
                mat.Params["LIGHTGRID_COLORS"] = cube;
            else
                mat.Params.Remove("LIGHTGRID_COLORS");
            foreach (var cb in mat.VsRefl.ConstantBuffers)
            {
                if (cb.BindPoint < 0) continue;
                var buf = ResolveCb(mat, cb, mat.VsCbs, s, placement, view, proj, unbound);
                if (buf.Handle is null) continue;
                _ctx.VSSetConstantBuffers((uint)cb.BindPoint, 1, ref buf);
            }
            foreach (var cb in mat.PsRefl.ConstantBuffers)
            {
                if (cb.BindPoint < 0) continue;
                var buf = ResolveCb(mat, cb, mat.PsCbs, s, placement, view, proj, unbound);
                if (buf.Handle is null) continue;
                _ctx.PSSetConstantBuffers((uint)cb.BindPoint, 1, ref buf);
            }
            if (debugBound) BindDebugPass(mat, s, frameWorld);
            _ctx.DrawIndexed(count, (uint)Math.Max(0, mat.StartIndex), 0);
            DrawCalls++;
            GeometryDraws++;
        }
        _instanceWorld = null;
    }

    private void DrawRiotMeshInstances(PreviewMaterial mat, int geometryId, PreviewSettings s,
        Matrix4x4 world, Matrix4x4 view, Matrix4x4 proj, List<string>? unbound)
    {
        if (geometryId < 0 || geometryId >= _riotMeshGeoms.Count || _riotMeshGeoms[geometryId] is not { } geom) return;
        var inst = mat.MeshInstances;
        int count = mat.MeshInstanceCount;
        if (inst is null || count <= 0) return;

        uint stride = PreviewVertex.SizeInBytes, offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref geom.Vb, in stride, in offset);
        _ctx.IASetIndexBuffer(geom.Ib, Format.FormatR32Uint, 0);

        var basis = new Matrix4x4(
            mat.MeshRight.X, mat.MeshRight.Y, mat.MeshRight.Z, 0f,
            mat.MeshUp.X, mat.MeshUp.Y, mat.MeshUp.Z, 0f,
            mat.MeshForward.X, mat.MeshForward.Y, mat.MeshForward.Z, 0f,
            0f, 0f, 0f, 1f);

        const int S = 19;   // ParticleQuadBuilder.Stride - the simulator's packed instance
        for (int i = 0; i < count; i++)
        {
            int o = i * S;
            if (o + S > inst.Length) break;
            // M640: per-axis scale - X and Y from the size slots, Z from slot 10 (the simulator packs a
            // mesh's Z scale where a quad keeps its flipbook frame). Magnitudes under 0.01 are clamped.
            static float Guard(float v) => MathF.Abs(v) < 0.01f ? MathF.CopySign(0.01f, v == 0f ? 1f : v) : v;
            var scale = new Vector3(Guard(inst[o + 3]), Guard(inst[o + 4]), Guard(inst[o + 10]));
            var pos = new Vector3(inst[o], inst[o + 1], inst[o + 2]);
            var euler = new Vector3(inst[o + 15], inst[o + 16], inst[o + 17]);
            var colour = new[] { inst[o + 5], inst[o + 6], inst[o + 7], inst[o + 8] };

            // Euler birth rotation (X, then Y, then Z - the order the quad path decoded from quad_vs; the
            // client composes a mesh's mWorld on the CPU, so the mesh order is NOT measurable from bytecode
            // and is taken to match), then the over-life spin about Y (slot 9, as GL), then the placement.
            var model = Matrix4x4.CreateScale(scale)   // a mirrored (negative) axis flips the winding; the
                                                       // rasterizer state is per material, so that case is
                                                       // still drawn with the unflipped cull (GL flips it)
                        * Matrix4x4.CreateRotationX(euler.X)
                        * Matrix4x4.CreateRotationY(euler.Y)
                        * Matrix4x4.CreateRotationZ(euler.Z)
                        * Matrix4x4.CreateRotationY(inst[o + 9])
                        * basis
                        * Matrix4x4.CreateTranslation(pos);
            if (mat.MeshParticleTransforms is { } transforms && i < transforms.Count) model = transforms[i];
            mat.Params["mWorld"] = Mat(model, s);
            mat.Params["kColorFactor"] = colour;
            // M641: the erosion drive. Riot's mesh_ps reads it from cAlphaErosionParams.x - the slot the
            // quad path leaves at zero, because THERE the drive arrives per vertex (quad_ps: mov o3.z,
            // v2.w). A mesh draw is already one particle, so the constant is the per-particle channel;
            // leaving it zero froze every mesh dissolve at drive 0, which is a static mask, not a
            // dissolve. Decoded from mesh_ps blob 777: te = cb0[0].x - E, then the two saturated ramps.
            if (mat.Params.TryGetValue("cAlphaErosionParams", out var erosionParams) && erosionParams.Length == 4)
                erosionParams[0] = inst[o + 18];

            foreach (var cb in mat.VsRefl.ConstantBuffers)
            {
                if (cb.BindPoint < 0) continue;
                if (!cb.Variables.Any(v => v.Name is "mWorld" or "kColorFactor" or "cAlphaErosionParams")) continue;
                var buf = ResolveCb(mat, cb, mat.VsCbs, s, world, view, proj, unbound);
                if (buf.Handle is null) continue;
                _ctx.VSSetConstantBuffers((uint)cb.BindPoint, 1, ref buf);
            }
            foreach (var cb in mat.PsRefl.ConstantBuffers)
            {
                if (cb.BindPoint < 0) continue;
                if (!cb.Variables.Any(v => v.Name is "mWorld" or "kColorFactor" or "cAlphaErosionParams")) continue;
                var buf = ResolveCb(mat, cb, mat.PsCbs, s, world, view, proj, unbound);
                if (buf.Handle is null) continue;
                _ctx.PSSetConstantBuffers((uint)cb.BindPoint, 1, ref buf);
            }

            _ctx.DrawIndexed((uint)geom.IndexCount, 0, 0);
            DrawCalls++;
            MeshDraws++;
        }
    }

    /// <summary>How many mesh-particle draws the last frame issued. One per PARTICLE, as GL does - mesh
    /// emitters are usually single-particle, but this is reported rather than assumed so a system that
    /// spawns many is visible as a cost rather than a mystery.</summary>
    public int MeshDraws { get; private set; }

    private bool DrawMeshParticles(PreviewMaterial mat, Matrix4x4 vp)
    {
        if (!EnsureMesh()) return false;
        if (mat.MeshGeometryId is not { } id || id < 0 || id >= _meshGeoms.Count) return false;
        var geom = _meshGeoms[id];
        if (geom is null) return false;
        if (geom.Vb.Handle is null) return false;
        // M295: two shapes of instance feed this one pipeline. Particles supply the simulator's packed
        // array (position + scalar scale + Y-spin); props supply real 4x4 placements. Props are the
        // MeshModels branch and leave every particle field neutral, so the two cannot interfere.
        var models = mat.MeshModels;
        var inst = mat.MeshInstances;
        int instanceCount = models is not null ? models.Count : mat.MeshInstanceCount;
        if (instanceCount == 0) return false;
        if (models is null && inst is null) return false;

        _ctx.IASetInputLayout(_meshLayout);
        _ctx.VSSetShader(_meshVs, null, 0);
        _ctx.PSSetShader(_meshPs, null, 0);
        _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        uint stride = MeshVertexStride * sizeof(float), offset = 0;
        _ctx.IASetVertexBuffers(0, 1, ref geom.Vb, in stride, in offset);
        if (geom.IndexCount > 0) _ctx.IASetIndexBuffer(geom.Ib, Format.FormatR32Uint, 0);

        var bound = BoundTexture(mat, "TEXTURE");
        var boundMult = BoundTexture(mat, "TEXTUREMULT");
        var tex = bound.Handle is not null ? bound : _white;
        var texMult = boundMult.Handle is not null ? boundMult : _white;
        var srvs = stackalloc ID3D11ShaderResourceView*[2];
        srvs[0] = tex; srvs[1] = texMult;
        _ctx.PSSetShaderResources(0, 2, srvs);
        var samp = _linearWrap;
        _ctx.PSSetSamplers(0, 1, ref samp);

        bool cutoutProp = mat.MeshModels is not null && mat.MeshAlphaCutoff > 0;
        var meshParticleState = mat.ParticleBlend is { } meshBlend ? ParticleBlendState(meshBlend) : default;   // M720
        _ctx.OMSetBlendState(cutoutProp && _blendOpaque.Handle is not null ? _blendOpaque
                : meshParticleState.Handle is not null ? meshParticleState
                : mat.Additive && _blendAdditive.Handle is not null ? _blendAdditive : _blend,
            stackalloc float[] { 0f, 0f, 0f, 0f }, 0xFFFFFFFF);
        _ctx.OMSetDepthStencilState(DepthStateFor(mat), 0);

        // M295: a prop material may draw only ONE SUBMESH of a shared geometry, because a prop's submeshes
        // each carry their own diffuse. Particles leave these at 0 and get the whole buffer, as before.
        int idxStart = Math.Max(0, mat.MeshIndexStart);
        int idxCount = mat.MeshIndexCount > 0
            ? Math.Min(mat.MeshIndexCount, Math.Max(0, geom.IndexCount - idxStart))
            : geom.IndexCount;

        int drawn = 0;
        var bytes = new byte[256];
        for (int i = 0; i < instanceCount; i++)
        {
            Matrix4x4 model;
            float scale, rot, cr, cg, cb, ca, px, py, pz;
            if (models is not null)
            {
                model = models[i];
                // Everything the particle path composes is neutralised: the placement matrix IS the
                // transform, and a prop is drawn at its authored colour.
                scale = 1f; rot = 0f; px = py = pz = 0f; cr = cg = cb = ca = 1f;
            }
            else
            {
                model = Matrix4x4.Identity;
                int o = i * MeshInstanceStride;
                scale = inst![o + 3];
                // GL clamps away from zero rather than skipping: a scale of exactly 0 would collapse the
                // mesh, and Riot authors 0 to mean "unscaled" often enough that dropping those loses real
                // geometry.
                if (MathF.Abs(scale) < 0.01f) scale = MathF.CopySign(0.01f, scale == 0f ? 1f : scale);
                px = inst[o + 0]; py = inst[o + 1]; pz = inst[o + 2];
                cr = inst[o + 5]; cg = inst[o + 6]; cb = inst[o + 7]; ca = inst[o + 8];
                rot = inst[o + 9];
            }

            var right = models is not null ? Vector3.UnitX : mat.MeshRight;
            var up = models is not null ? Vector3.UnitY : mat.MeshUp;
            var fwd = models is not null ? Vector3.UnitZ : mat.MeshForward;

            var vals = new[]
            {
                vp.M11, vp.M12, vp.M13, vp.M14, vp.M21, vp.M22, vp.M23, vp.M24,
                vp.M31, vp.M32, vp.M33, vp.M34, vp.M41, vp.M42, vp.M43, vp.M44,
                model.M11, model.M12, model.M13, model.M14, model.M21, model.M22, model.M23, model.M24,
                model.M31, model.M32, model.M33, model.M34, model.M41, model.M42, model.M43, model.M44,
                right.X, right.Y, right.Z, 0f,
                up.X, up.Y, up.Z, 0f,
                fwd.X, fwd.Y, fwd.Z, 0f,
                px, py, pz, scale,
                cr, cg, cb, ca,
                mat.MeshUvOffset.X, mat.MeshUvOffset.Y, mat.MeshTexDiv.X, mat.MeshTexDiv.Y,
                mat.MeshUvOffsetMult.X, mat.MeshUvOffsetMult.Y, mat.MeshTexDivMult.X, mat.MeshTexDivMult.Y,
                rot, boundMult.Handle is not null ? 1f : 0f, mat.MeshAlphaCutoff, 0f,
            };
            System.Buffer.BlockCopy(vals, 0, bytes, 0, 256);
            Upload(_meshCb, bytes, 256);
            _ctx.VSSetConstantBuffers(0, 1, ref _meshCb);
            _ctx.PSSetConstantBuffers(0, 1, ref _meshCb);

            if (mat.MeshCull)
            {
                // A negative determinant reverses winding, so the correct faces would otherwise be the
                // ones discarded. GL checks the same thing per prop instance.
                float det = models is not null ? model.GetDeterminant() : scale;
                _ctx.RSSetState(det < 0f ? _meshCullCcw : _meshCullCw);
            }
            else _ctx.RSSetState(_raster);

            if (idxCount > 0) _ctx.DrawIndexed((uint)idxCount, (uint)idxStart, 0);
            else _ctx.Draw((uint)geom.VertexCount, 0);
            drawn++;
        }

        // Put the shared rasterizer back, or every draw after this one inherits mesh culling.
        _ctx.RSSetState(_raster);
        MeshDraws += drawn;
        return drawn > 0;
    }

    /// <summary>The view bound for a sampler, by the names Riot's shaders declare. Matching on a PREFIX
    /// would be wrong here: "TEXTURE" is a prefix of "TEXTUREMULT", so a prefix test can hand back the
    /// multiply texture when the diffuse was wanted, silently, on exactly the emitters that author both.</summary>
    /// <summary>M711: which of the three depth states this material draws with. One chooser, because the two
    /// draw sites had hand-copied the two-state expression and a third state would have been added to one of
    /// them.</summary>
    private ComPtr<ID3D11DepthStencilState> DepthStateFor(PreviewMaterial mat)
    {
        if (!mat.TestsDepth && _depthStateNoTest.Handle is not null) return _depthStateNoTest;
        return mat.WritesDepth || _depthStateNoWrite.Handle is null ? _depthState : _depthStateNoWrite;
    }

    private ComPtr<ID3D11ShaderResourceView> BoundTexture(PreviewMaterial mat, string sampler)
    {
        if (mat.Textures.TryGetValue(sampler + "__TX", out var a) && a.Handle is not null) return a;
        if (mat.Textures.TryGetValue(sampler, out var b) && b.Handle is not null) return b;
        return default;
    }

    /// <summary>Floats per mesh particle instance, matching the simulator's packed layout so the App layer
    /// can hand over a slice of it unchanged: [x,y,z, sizeX,sizeY, r,g,b,a, rot, frame].</summary>
    public const int MeshInstanceStride = 11;

    /// <summary>M282, corrected in M461: the heat-haze pass.
    ///
    /// <para><b>This is a transcription of Riot's <c>particlesystem/distortion_ps</c> blob 0</b>, not a
    /// port of the GL original it started as. ReyEngine draws heat haze with its own HLSL rather than
    /// Riot's blob only because Riot's <c>distortion_vs</c> wants a vertex stream the shared quad buffer
    /// does not carry; every line of the pixel maths below has a counterpart in
    /// <see cref="ParticleShading"/>, which carries the disassembly line numbers and is what the tests and
    /// the <c>distortgpu</c> probe hold this to.</para>
    ///
    /// <para>M461 fixed three measured divergences (frame-pipeline.md §5.2 item 3). The offset is scaled by
    /// the colour-over-life ramp's alpha alone - not by <c>normal.a * diffuse.a</c>, which Riot does not
    /// have there. The refracted sample is TINTED by <c>diffuse.rgb * vertexColour.rgb * ramp.rgb</c>
    /// rather than returned raw. And the output alpha is <c>normal.a * ramp.a</c>, so the shape comes from
    /// the normal map rather than from a diffuse that is routinely a deliberate blank.</para>
    ///
    /// <para>The DIFFUSE texture now contributes its RGB, and only its RGB. A heat-haze emitter can ship a
    /// blank sprite (Jade_FireTorch_Med's is an 8x8 all-white "color-hold") and still look right, because
    /// white is the tint's identity - drawing that sprite normally instead is what produces a solid white
    /// card, which is the bug M282 fixed and this keeps fixed.</para>
    ///
    /// <para>SV_Position.y needs no flip. GL's gl_FragCoord is bottom-up and D3D's SV_Position is top-down,
    /// but the scene copy is stored in the same top-down order the target was rendered in, so screen
    /// position and scene texel agree in both APIs without a correction. Adding one would tear the
    /// refraction vertically.</para></summary>
    private const string DistortHlsl = @"
cbuffer DistortCB : register(b0)
{
    row_major float4x4 gMvp;
    float4 gParams;      // x = strength (Riot's DistortionPower), yz = 1/viewport, w unused
};
Texture2D gScene   : register(t0);
Texture2D gNormal  : register(t1);
Texture2D gDiffuse : register(t2);
SamplerState gClamp : register(s0);
SamplerState gWrap  : register(s1);

struct VIn  { float3 pos : POSITION; float2 uv : TEXCOORD0; float4 col : COLOR; };
struct VOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 col : COLOR; };

VOut vsmain(VIn i)
{
    VOut o;
    o.pos = mul(float4(i.pos, 1.0), gMvp);
    o.uv = i.uv;
    o.col = i.col;
    return o;
}

float4 psmain(VOut i) : SV_Target
{
    float4 n = gNormal.Sample(gWrap, i.uv);
    float4 t = gDiffuse.Sample(gWrap, i.uv);

    // ParticleShading.SubstituteRamp. Riot samples PARTICLE_COLOR_TEXTURE at uv1; ReyEngine's uv1 is
    // still the sprite corner UV, so the texture is deliberately not bound and the vertex alpha - which
    // already carries the same authored colorOverLife curve - stands in for the ramp alpha.
    float4 p = float4(1.0, 1.0, 1.0, i.col.a);

    float2 offset = (n.rg - 0.5) * gParams.x * p.a * 2.0;
    float2 sceneUv = clamp(i.pos.xy * gParams.yz + offset, 0.0, 1.0);
    float3 bb = gScene.Sample(gClamp, sceneUv).rgb;
    float3 tint = t.rgb * i.col.rgb * p.rgb;
    return float4(bb * tint, n.a * p.a);
}";

    /// <summary>M461: the shipped heat-haze source, so the <c>distortgpu</c> probe compiles THIS string
    /// rather than a copy of it. A probe that transcribes the shader can only ever prove the transcription
    /// right, which is the one thing that was never in doubt.</summary>
    public static string DistortionHlslForTests => DistortHlsl;

    private bool EnsureDistort()
    {
        if (_distortTried) return _distortVs.Handle is not null;
        _distortTried = true;

        ID3D10Blob* vsCode = null, psCode = null, errs = null;
        var src = System.Text.Encoding.ASCII.GetBytes(DistortHlsl);
        try
        {
            var compiler = D3DCompiler.GetApi();
            fixed (byte* sp = src)
            {
                var vsEntry = System.Text.Encoding.ASCII.GetBytes("vsmain\0");
                var vsTarget = System.Text.Encoding.ASCII.GetBytes("vs_5_0\0");
                fixed (byte* ep = vsEntry) fixed (byte* tp = vsTarget)
                    if (compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                            ep, tp, 0u, 0u, &vsCode, &errs) < 0 || vsCode is null)
                    { Log("distortion vs failed to compile"); return false; }

                var psEntry = System.Text.Encoding.ASCII.GetBytes("psmain\0");
                var psTarget = System.Text.Encoding.ASCII.GetBytes("ps_5_0\0");
                fixed (byte* ep = psEntry) fixed (byte* tp = psTarget)
                    if (compiler.Compile(sp, (nuint)src.Length, (byte*)null, null, (ID3DInclude*)null,
                            ep, tp, 0u, 0u, &psCode, &errs) < 0 || psCode is null)
                    { Log("distortion ps failed to compile"); return false; }
            }
        }
        catch (Exception ex) { Log("distortion: the HLSL compiler is unavailable: " + ex.Message); return false; }

        ComPtr<ID3D11VertexShader> vs = default;
        if (_device.CreateVertexShader(vsCode->GetBufferPointer(), vsCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref vs) < 0)
        { Log("distortion CreateVertexShader failed"); return false; }
        _distortVs = vs;

        ComPtr<ID3D11PixelShader> ps = default;
        if (_device.CreatePixelShader(psCode->GetBufferPointer(), psCode->GetBufferSize(),
                ref Unsafe.NullRef<ID3D11ClassLinkage>(), ref ps) < 0)
        { Log("distortion CreatePixelShader failed"); return false; }
        _distortPs = ps;

        // Read out of the same fat vertex the particle quads already fill, at the same offsets the material
        // path maps these semantics to - so the distortion draw sees byte-identical geometry to the one the
        // ordinary billboard path would have drawn, and cannot drift from it.
        var semPos = System.Text.Encoding.ASCII.GetBytes("POSITION\0");
        var semUv = System.Text.Encoding.ASCII.GetBytes("TEXCOORD\0");
        var semCol = System.Text.Encoding.ASCII.GetBytes("COLOR\0");
        fixed (byte* sp0 = semPos)
        fixed (byte* sp1 = semUv)
        fixed (byte* sp2 = semCol)
        {
            var els = stackalloc InputElementDesc[3];
            els[0] = new InputElementDesc
            {
                SemanticName = sp0, SemanticIndex = 0, Format = Format.FormatR32G32B32Float,
                InputSlot = 0, AlignedByteOffset = 0,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            els[1] = new InputElementDesc
            {
                SemanticName = sp1, SemanticIndex = 0, Format = Format.FormatR32G32Float,
                InputSlot = 0, AlignedByteOffset = 40,      // PreviewVertex.Uv0, a float4; two components used
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            els[2] = new InputElementDesc
            {
                SemanticName = sp2, SemanticIndex = 0, Format = Format.FormatR32G32B32A32Float,
                InputSlot = 0, AlignedByteOffset = 88,      // PreviewVertex.Color
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            ComPtr<ID3D11InputLayout> layout = default;
            if (_device.CreateInputLayout(els, 3, vsCode->GetBufferPointer(), vsCode->GetBufferSize(), ref layout) < 0)
            { Log("distortion CreateInputLayout failed"); return false; }
            _distortLayout = layout;
        }

        var cbDesc = new BufferDesc
        {
            ByteWidth = 80,                                  // float4x4 + float4
            Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer,
            CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> cb = default;
        if (_device.CreateBuffer(in cbDesc, null, ref cb) < 0) { Log("distortion cbuffer failed"); return false; }
        _distortCb = cb;

        Log("distortion pipeline built");
        return true;
    }

    /// <summary>Snapshot the colour target so a distortion draw has something to refract. Taken lazily, at
    /// the first distortion material rather than before the whole frame, so what gets refracted is
    /// everything drawn UNDER the emitter - map, and any particle already composited - which is what
    /// refraction means. The render targets are unbound across the copy: reading a resource that is
    /// simultaneously bound for output is a hazard the debug layer rejects outright.</summary>
    /// <summary>How many heat-haze draws the last frame issued. Zero while a scene holds distortion
    /// emitters means the pass is being skipped, which is worth being able to see from a test.</summary>
    public int DistortionDraws { get; private set; }

    /// <summary>Draw one heat-haze slice. Returns false when the pass cannot run - no compiled pipeline, no
    /// scene copy, or no normal map - in which case the emitter is drawn NOT AT ALL rather than falling
    /// back to the ordinary billboard path. That is deliberate and matches GL, which skips the emitter
    /// outright under the same conditions (VfxParticleRenderer.cs:381): the fallback is what produces the
    /// solid white card, so it is worse than drawing nothing.</summary>
    private bool DrawDistortion(PreviewMaterial mat, float strength, Matrix4x4 vp,
                                int width, int height, ref int boundSource)
    {
        if (!EnsureDistort() || _sceneCopySrv.Handle is null) return false;
        if (!mat.Textures.TryGetValue(PreviewMaterial.DistortionNormalKey, out var normal)) return false;

        uint count = mat.IndexCount < 0
            ? (uint)(mat.UsesDynamicMesh ? _dynIndexCount : _indexCount)
            : (uint)mat.IndexCount;
        if (count == 0 || !BindMeshSource(mat.UsesDynamicMesh, ref boundSource)) return false;

        var bytes = new byte[80];
        var vals = new[]
        {
            vp.M11, vp.M12, vp.M13, vp.M14, vp.M21, vp.M22, vp.M23, vp.M24,
            vp.M31, vp.M32, vp.M33, vp.M34, vp.M41, vp.M42, vp.M43, vp.M44,
            strength, 1f / Math.Max(1, width), 1f / Math.Max(1, height), 0f,
        };
        System.Buffer.BlockCopy(vals, 0, bytes, 0, 80);
        Upload(_distortCb, bytes, 80);

        _ctx.IASetInputLayout(_distortLayout);
        _ctx.VSSetShader(_distortVs, null, 0);
        _ctx.PSSetShader(_distortPs, null, 0);
        _ctx.VSSetConstantBuffers(0, 1, ref _distortCb);
        _ctx.PSSetConstantBuffers(0, 1, ref _distortCb);

        // M461: the emitter's own diffuse, for its RGB - it is one of the three factors in Riot's tint
        // (diffuse x vertexColour x ramp). Its ALPHA is no longer read at all: Riot's blob 0 takes the
        // output alpha from the normal map, and a heat-haze diffuse is routinely a deliberate blank whose
        // alpha carries no information. An emitter that ships no diffuse gets the opaque white stand-in,
        // which is the tint's identity and leaves the refraction untinted.
        // M707: an emitter that names NO texture draws nothing at all. The engine binds a transparent texel
        // on its base slot, and the OpenGL viewport's heat-haze masks by that texel's alpha and so shows
        // nothing either. Tinting the refraction by it here would paint a BLACK smear instead, because this
        // pass takes its output alpha from the normal map and would keep it. The white identity below still
        // stands for the case it was written for: a slot with nothing bound on it at all.
        if (mat.BaseTextureIsUnnamed) return false;
        var diffuse = BoundTexture(mat, "TEXTURE");
        if (diffuse.Handle is null) diffuse = _white;

        var srvs = stackalloc ID3D11ShaderResourceView*[3];
        srvs[0] = _sceneCopySrv; srvs[1] = normal; srvs[2] = diffuse;
        _ctx.PSSetShaderResources(0, 3, srvs);

        var samplers = stackalloc ID3D11SamplerState*[2];
        samplers[0] = _linearClamp; samplers[1] = _linearWrap;
        _ctx.PSSetSamplers(0, 2, samplers);

        // Straight alpha, never additive - see PreviewMaterial.DistortionStrength for why the authored
        // blendMode must not reach this draw. Depth is tested but not written, as for every particle.
        _ctx.OMSetBlendState(_blend, stackalloc float[] { 0f, 0f, 0f, 0f }, 0xFFFFFFFF);
        // M720: still never a depth write, whatever the mode - but the M711 test flag reaches heat haze now,
        // as it reaches every other particle draw.
        _ctx.OMSetDepthStencilState(
            !mat.TestsDepth && _depthStateNoTest.Handle is not null ? _depthStateNoTest
            : _depthStateNoWrite.Handle is not null ? _depthStateNoWrite : _depthState, 0);

        _ctx.DrawIndexed(count, (uint)Math.Max(0, mat.StartIndex), 0);

        // Unbind the scene copy. It is the resource CopyResource writes into on the next distortion draw,
        // and leaving it bound as an SRV while it is a copy destination is the same read/write hazard the
        // capture avoids on the target - it would simply be reported one draw later.
        var none = stackalloc ID3D11ShaderResourceView*[3];
        none[0] = null; none[1] = null; none[2] = null;
        _ctx.PSSetShaderResources(0, 3, none);
        return true;
    }

    private void CaptureSceneCopy()
    {
        if (_sceneCopy.Handle is null || _rt.Handle is null) return;
        _ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        _ctx.CopyResource(_sceneCopy, _rt);
        BindSceneTargets();
    }

    /// <summary>
    /// M460: the ONE place the scene's render targets are bound, so RT1 cannot be dropped by accident.
    ///
    /// <para>This used to be a literal <c>OMSetRenderTargets(1, ref _rtv, _dsv)</c> in three places: once
    /// before the draw loop and once in each of the two capture helpers, which unbind everything so a
    /// <c>CopyResource</c> can run and then put the targets back. Both helpers are called from INSIDE the
    /// material loop - the first heat-haze material and the first soft-particle material - so with the glow
    /// buffer bound, a literal rebind of one RTV there would silently switch RT1 off partway through the
    /// frame. Every emissive material sorted after that point would stop contributing, and the bloom would
    /// come and go depending on which materials a camera angle happened to make visible.</para>
    /// </summary>
    private void BindSceneTargets()
    {
        if (_glowBound && _glowRtv.Handle is not null)
        {
            var rtvs = stackalloc ID3D11RenderTargetView*[2];
            rtvs[0] = _rtv; rtvs[1] = _glowRtv;
            _ctx.OMSetRenderTargets(2, rtvs, _dsv);
            return;
        }
        _ctx.OMSetRenderTargets(1, ref _rtv, _dsv);
    }

    private void EnsureTargets(int w, int h)
    {
        if (_width == w && _height == h && _rt.Handle is not null) return;
        _rtv.Dispose(); _rt.Dispose(); _stage.Dispose(); _dsv.Dispose(); _depth.Dispose();
        _rtv = default; _rt = default; _stage = default; _dsv = default; _depth = default;
        _sceneCopySrv.Dispose(); _sceneCopy.Dispose();
        _sceneCopySrv = default; _sceneCopy = default;
        _depthCopySrv.Dispose(); _depthCopy.Dispose();
        _depthCopySrv = default; _depthCopy = default;
        _width = w; _height = h;

        // BGRA so the readback drops straight into an Avalonia Bgra8888 bitmap with no swizzle
        var rtDesc = new Texture2DDesc
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.FormatB8G8R8A8Unorm, SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default, BindFlags = (uint)BindFlag.RenderTarget,
        };
        ComPtr<ID3D11Texture2D> rt = default;
        _device.CreateTexture2D(in rtDesc, null, ref rt);
        _rt = rt;
        ComPtr<ID3D11RenderTargetView> rtv = default;
        _device.CreateRenderTargetView(_rt, null, ref rtv);
        _rtv = rtv;

        var st = rtDesc;
        st.Usage = Usage.Staging; st.BindFlags = 0; st.CPUAccessFlags = (uint)CpuAccessFlag.Read;
        ComPtr<ID3D11Texture2D> stage = default;
        _device.CreateTexture2D(in st, null, ref stage);
        _stage = stage;

        // M282: the distortion scene copy. Same format and size as the target, which is what lets the copy
        // be a straight CopyResource rather than a draw. Allocated with the targets rather than lazily, so
        // a resize can never leave a distortion draw sampling a stale-sized view.
        var sc = rtDesc;
        sc.BindFlags = (uint)BindFlag.ShaderResource;
        ComPtr<ID3D11Texture2D> scene = default;
        if (_device.CreateTexture2D(in sc, null, ref scene) >= 0)
        {
            _sceneCopy = scene;
            ComPtr<ID3D11ShaderResourceView> ssrv = default;
            if (_device.CreateShaderResourceView(_sceneCopy, null, ref ssrv) >= 0) _sceneCopySrv = ssrv;
            else Log("distortion: CreateShaderResourceView for the scene copy failed");
        }
        else Log("distortion: the scene-copy texture could not be created; heat haze will be skipped");

        // M363: R32_TYPELESS, not D32_FLOAT. Identical precision and identical depth behaviour, but a fully
        // typed depth format can never carry a shader-resource view, and soft particles have to SAMPLE this.
        // The DSV below names D32_FLOAT explicitly, which is what the typeless format defers.
        var dd = new Texture2DDesc
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.FormatR32Typeless, SampleDesc = new SampleDesc(1, 0),
            Usage = Usage.Default, BindFlags = (uint)BindFlag.DepthStencil,
        };
        ComPtr<ID3D11Texture2D> depth = default;
        _device.CreateTexture2D(in dd, null, ref depth);
        _depth = depth;
        var dsvDesc = new DepthStencilViewDesc
        {
            Format = Format.FormatD32Float,
            ViewDimension = DsvDimension.Texture2D,
        };
        ComPtr<ID3D11DepthStencilView> dsv = default;
        _device.CreateDepthStencilView(_depth, in dsvDesc, ref dsv);
        _dsv = dsv;

        // M363: a COPY, for the same reason M282's colour capture is a copy - a resource cannot be bound as
        // a depth-stencil view and read as a shader resource in the same draw, and the particles that want
        // to read this are being drawn INTO that very depth buffer. The GL path resolves it the same way,
        // with a depth blit into its own texture.
        var dc = dd;
        dc.BindFlags = (uint)BindFlag.ShaderResource;
        ComPtr<ID3D11Texture2D> dcopy = default;
        if (_device.CreateTexture2D(in dc, null, ref dcopy) >= 0)
        {
            _depthCopy = dcopy;
            var dsrv = new ShaderResourceViewDesc
            {
                Format = Format.FormatR32Float,
                ViewDimension = D3DSrvDimension.D3D11SrvDimensionTexture2D,
                Anonymous = new ShaderResourceViewDescUnion
                {
                    Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1 },
                },
            };
            ComPtr<ID3D11ShaderResourceView> dsv2 = default;
            if (_device.CreateShaderResourceView(_depthCopy, in dsrv, ref dsv2) >= 0) _depthCopySrv = dsv2;
            else Log("soft particles: CreateShaderResourceView for the depth copy failed");
        }
        else Log("soft particles: the depth-copy texture could not be created; the fade will stay neutral");

        // M460: drop the glow buffer and its mip chain. They are rebuilt lazily, from _width/_height, by
        // the first frame that actually wants them - but they MUST go here, because a glow target left at
        // the old size would be a different size from RT0, and D3D11 rejects a mismatched set at bind time,
        // taking the whole scene down with the bloom.
        ResetBloomTargets();
    }

    /// <summary>M363: snapshot the depth buffer so particles can sample the scene behind them. Called from
    /// the draw loop the first time a soft-particle material is reached, which is after the opaque geometry
    /// has written depth and before any particle has - exactly the window the effect needs.</summary>
    private void CaptureDepthCopy()
    {
        if (_depthCopy.Handle is null || _depth.Handle is null) return;
        _ctx.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        _ctx.CopyResource(_depthCopy, _depth);
        BindSceneTargets();
    }

    /// <summary>M363, moved to <see cref="ParticleShading.DepthConversion"/> in M461 so the soft-particle
    /// arithmetic lives in one asserted place. This adapts it to the <c>float[]</c> the constant feed
    /// wants.</summary>
    private static float[] DepthConversionFrom(Matrix4x4 proj)
    {
        var dc = ParticleShading.DepthConversion(proj);
        return new[] { dc.X, dc.Y, dc.Z, dc.W };
    }

    private (bool wire, bool cull, bool depth, bool blend, bool mirror)? _stateKey;

    private static Blend D3DColorBlend(MaterialBlendFactor factor) => factor switch
    {
        MaterialBlendFactor.Zero => Blend.Zero,
        MaterialBlendFactor.One => Blend.One,
        MaterialBlendFactor.SourceColor => Blend.SrcColor,
        MaterialBlendFactor.OneMinusSourceColor => Blend.InvSrcColor,
        MaterialBlendFactor.DestinationColor => Blend.DestColor,
        MaterialBlendFactor.OneMinusDestinationColor => Blend.InvDestColor,
        MaterialBlendFactor.SourceAlpha => Blend.SrcAlpha,
        MaterialBlendFactor.OneMinusSourceAlpha => Blend.InvSrcAlpha,
        MaterialBlendFactor.DestinationAlpha => Blend.DestAlpha,
        MaterialBlendFactor.OneMinusDestinationAlpha => Blend.InvDestAlpha,
        _ => Blend.One,
    };

    private ComPtr<ID3D11BlendState> AuthoredBlendState(
        MaterialBlendFactor source, MaterialBlendFactor destination)
    {
        var key = (source, destination);
        if (_authoredBlendStates.TryGetValue(key, out var existing)) return existing;

        var desc = new BlendDesc();
        desc.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = true,
            SrcBlend = D3DColorBlend(source), DestBlend = D3DColorBlend(destination), BlendOp = BlendOp.Add,
            // StaticMaterialDef only authors the COLOR factors. Keep the established alpha equation;
            // it preserves coverage without inventing a second enum from absent data.
            SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.InvSrcAlpha, BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ComPtr<ID3D11BlendState> state = default;
        if (_device.CreateBlendState(in desc, ref state) < 0) return default;
        _authoredBlendStates[key] = state;
        return state;
    }

    /// <summary>
    /// M720: the device state for one particle blend. The colour factors and equation are the mode's. The
    /// alpha lane is Zero, One with an Add equation and alpha is masked out of the write besides: this target
    /// is read back into a premultiplied bitmap, so a particle that lowered its alpha would show the window
    /// through, and under MIN or MAX the factors that pin the lane are ignored - only the mask holds there.
    /// </summary>
    private ComPtr<ID3D11BlendState> ParticleBlendState(ReyEngine.Formats.Vfx.VfxBlendState st)
    {
        var key = (st.Enabled, st.Src, st.Dst, st.Op, st.WritesColor);
        if (_particleBlendStates.TryGetValue(key, out var existing)) return existing;

        var desc = new BlendDesc();
        desc.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = st.Enabled,
            SrcBlend = D3DBlend(st.Src), DestBlend = D3DBlend(st.Dst),
            BlendOp = st.Op switch
            {
                ReyEngine.Formats.Vfx.VfxBlendOp.Min => BlendOp.Min,
                ReyEngine.Formats.Vfx.VfxBlendOp.Max => BlendOp.Max,
                _ => BlendOp.Add,
            },
            SrcBlendAlpha = Blend.Zero, DestBlendAlpha = Blend.One, BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = st.WritesColor
                ? (byte)(ColorWriteEnable.Red | ColorWriteEnable.Green | ColorWriteEnable.Blue)
                : (byte)0,
        };
        ComPtr<ID3D11BlendState> state = default;
        if (_device.CreateBlendState(in desc, ref state) < 0) return default;
        _particleBlendStates[key] = state;
        return state;
    }

    private static Blend D3DBlend(ReyEngine.Formats.Vfx.VfxBlendFactor factor) => factor switch
    {
        ReyEngine.Formats.Vfx.VfxBlendFactor.Zero => Blend.Zero,
        ReyEngine.Formats.Vfx.VfxBlendFactor.SrcAlpha => Blend.SrcAlpha,
        ReyEngine.Formats.Vfx.VfxBlendFactor.InvSrcAlpha => Blend.InvSrcAlpha,
        ReyEngine.Formats.Vfx.VfxBlendFactor.InvSrcColor => Blend.InvSrcColor,
        ReyEngine.Formats.Vfx.VfxBlendFactor.DestAlpha => Blend.DestAlpha,
        ReyEngine.Formats.Vfx.VfxBlendFactor.InvDestAlpha => Blend.InvDestAlpha,
        _ => Blend.One,
    };

    private void UpdateStates(PreviewSettings s)
    {
        // M216: these were disposed and recreated every single frame. They only depend on four toggles.
        var key = (s.Wireframe, s.CullBackFaces, s.DepthTest, s.AlphaBlend, s.MirrorX);
        if (_stateKey == key && _raster.Handle is not null) return;
        _stateKey = key;

        _raster.Dispose(); _blend.Dispose(); _blendOpaque.Dispose(); _depthState.Dispose();
        _raster = default; _blend = default; _blendOpaque = default; _depthState = default;

        var rd = new RasterizerDesc
        {
            FillMode = s.Wireframe ? FillMode.Wireframe : FillMode.Solid,
            CullMode = s.CullBackFaces ? CullMode.Back : CullMode.None,
            // M223: a mirrored view reverses triangle winding, so the front face has to swap with it or
            // backface culling removes exactly the faces it should keep. ViewportMeshRenderer does the same
            // thing off the model determinant.
            //
            // M357: INVERTED from M223, measured. M223 got the swap direction right but the base convention
            // wrong: it assumed the unmirrored front face is clockwise (D3D's default), so mirrored had to
            // become counter-clockwise. League's geometry is authored CCW-front - GL renders it correctly
            // with a plain CullFace(Back) under its CCW-front default - so mirroring makes the front CW,
            // which is the opposite.
            //
            // It went unnoticed for so long because nothing exercised it: the map host pins CullBackFaces
            // off, and with CullMode.None the winding is irrelevant. Turning culling on (M354) executed
            // this line for the first time and deleted the terrain. Measured directly afterwards: with the
            // Cull Back Faces toggle on a mirrored map, this flag TRUE removes the surfaces that should
            // remain, so the correct value under a mirror is false.
            FrontCounterClockwise = !s.MirrorX,
            DepthClipEnable = 1,
        };
        ComPtr<ID3D11RasterizerState> rs = default;
        _device.CreateRasterizerState(in rd, ref rs);
        _raster = rs;

        // M354: the same state with culling FORCED ON, for materials whose bin says cullEnable=true.
        // Riot authors most map surfaces single-sided; drawing them two-sided lets interior faces show
        // through and lights back faces that the game never rasterises. GL has picked per submesh since
        // M34 (cull = cullBackfaces && !DoubleSided) - this is the D3D11 half of that, and the reason a
        // SECOND state exists rather than a flag on the first is that D3D11 cull mode lives in immutable
        // rasterizer state, so per-draw selection means per-draw objects.
        _rasterCull.Dispose();
        rd.CullMode = CullMode.Back;
        ComPtr<ID3D11RasterizerState> rsCull = default;
        _device.CreateRasterizerState(in rd, ref rsCull);
        _rasterCull = rsCull;

        var bd = new BlendDesc();
        bd.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = s.AlphaBlend,
            SrcBlend = Blend.SrcAlpha, DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.InvSrcAlpha, BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ComPtr<ID3D11BlendState> bs = default;
        _device.CreateBlendState(in bd, ref bs);
        _blend = bs;

        var obd = new BlendDesc();
        obd.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = false,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        ComPtr<ID3D11BlendState> obs = default;
        _device.CreateBlendState(in obd, ref obs);
        _blendOpaque = obs;

        // M232: additive, for particle emitters whose blendMode says so. Same source factor, but the
        // destination ADDS instead of being scaled down, and alpha is left alone.
        var abd = new BlendDesc();
        abd.RenderTarget[0] = new RenderTargetBlendDesc
        {
            BlendEnable = 1,
            SrcBlend = Blend.SrcAlpha, DestBlend = Blend.One, BlendOp = BlendOp.Add,
            SrcBlendAlpha = Blend.Zero, DestBlendAlpha = Blend.One, BlendOpAlpha = BlendOp.Add,
            RenderTargetWriteMask = (byte)ColorWriteEnable.All,
        };
        _blendAdditive.Dispose();
        ComPtr<ID3D11BlendState> abs = default;
        _device.CreateBlendState(in abd, ref abs);
        _blendAdditive = abs;

        // M541: LessEqual, not Less - the same function GL has always used for the scene
        // (ViewportMeshRenderer sets DepthFunction.Lequal). A DECAL is coplanar with the ground it sits
        // on by construction, so under Less its fragments compare equal and FAIL, and the decal is not
        // drawn at all. That is exactly the reported "decals are invisible in DX11 but fine in OpenGL,
        // and I have to move them up to see them" - raising a decal is a way of turning equal into less.
        //
        // The game itself evidently draws these decals over coplanar ground, so LessEqual is also what
        // the target renders with. The cost is that coincident OPAQUE surfaces now resolve by draw order
        // rather than by rejection; GL has carried that trade since the beginning and the two viewports
        // disagreeing about whether geometry exists is the worse of the two problems.
        var dsd = new DepthStencilDesc
        {
            DepthEnable = s.DepthTest,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunc.LessEqual,
            StencilEnable = 0,
        };
        ComPtr<ID3D11DepthStencilState> ds = default;
        _device.CreateDepthStencilState(in dsd, ref ds);
        _depthState = ds;

        // M266: the no-write twin. StateDescription.Particle already declares "no depth write", but that is
        // only a PIPELINE CACHE KEY - its sole consumer is PipelineKey.For - and it was never applied as
        // device state. The preview window hid that by turning depth test off entirely for the Particles
        // preset; the map viewport keeps depth test on, so the write has to be masked instead.
        var dsdNoWrite = dsd;
        dsdNoWrite.DepthWriteMask = DepthWriteMask.Zero;
        _depthStateNoWrite.Dispose();
        ComPtr<ID3D11DepthStencilState> dsn = default;
        _device.CreateDepthStencilState(in dsdNoWrite, ref dsn);
        _depthStateNoWrite = dsn;

        // M711: no test and no write, for an emitter carrying the engine's DISABLE_ZBUFFER bit. It is the
        // no-write state with the test switched off rather than a third independent description, so the two
        // can never drift apart on the comparison function.
        var dsdNoTest = dsdNoWrite;
        dsdNoTest.DepthEnable = 0;
        _depthStateNoTest.Dispose();
        ComPtr<ID3D11DepthStencilState> dsnt = default;
        _device.CreateDepthStencilState(in dsdNoTest, ref dsnt);
        _depthStateNoTest = dsnt;
    }

    // ---------------------------------------------------------------- constants

    /// <summary>User/material overrides by constant name, e.g. <c>TintColor</c> → 4 floats.</summary>
    public Dictionary<string, float[]> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fill one reflected cbuffer. Values come from, in order: an explicit override, the engine
    /// value for a name we recognise, the constant's own RDEF default, then zero.</summary>
    private void FillConstantBuffer(PreviewMaterial? mat, DxbcConstantBuffer cb, PreviewSettings s,
        Matrix4x4 world, Matrix4x4 view, Matrix4x4 proj, List<string>? unbound)
    {
        // M216: reused, not reallocated. At 1,600 draw calls x 2-4 cbuffers this was thousands of
        // short-lived arrays per frame and the GC pressure showed up directly in the frame time.
        int need = Math.Max(16, cb.AllocationSize);
        if (_cbScratch.Length < need) _cbScratch = new byte[need];
        var bytes = _cbScratch;
        Array.Clear(bytes, 0, need);
        var vp = Matrix4x4.Multiply(view, proj);
        var cam = ShaderCamera(s);

        // M214: the bone palette. A skinned character's vertex shader transforms every vertex by the bones
        // its BLENDINDICES name, so a zero-filled BonesCB collapses the whole mesh to the origin and draws
        // nothing at all - which is exactly what happened first. There is no animation here, so the correct
        // content is the identity bind pose: the mesh renders as authored.
        if (cb.Name.Contains("Bone", StringComparison.OrdinalIgnoreCase))
        {
            int rows = Math.Clamp(s.BoneMatrixRows, 3, 4);
            int stride = rows * 16;
            var palette = mat?.BonePalette ?? s.BonePalette;   // M676: a prop's own palette, else the frame's

            // M615: the whole reason this reduces to the M216 constant is what makes it safe. With no
            // animation every skinning matrix is identity, so skin * view IS view, and an animated palette
            // is the same expression with the identity replaced. The known-good bind-pose case is a
            // special case of this rather than a separate branch.
            var baseM = s.BonePose switch
            {
                // M620: the model transform belongs here, ahead of the view - it is the only matrix a
                // skinned character shader multiplies by. Identity World leaves this exactly as it was.
                // M676: a CharacterInstances material is mid-loop over its placements, and the placement
                // is the model transform - not the frame's World, which is the character window's one model.
                BonePose.View or BonePose.ViewTransposed =>
                    (_instanceWorld ?? s.World).IsIdentity ? view : (_instanceWorld ?? s.World) * view,
                _ => _instanceWorld ?? s.World,
            };
            bool transpose = s.BonePose is BonePose.ViewTransposed or BonePose.WorldTransposed;

            var slot = new float[16];
            int index = 0;
            for (int at = 0; at + stride <= bytes.Length; at += stride, index++)
            {
                // Past the end of the palette the remaining slots stay at the bind pose. A shader may
                // index a bone the skeleton does not have, and zeroes there collapse that vertex to the
                // origin and streak a triangle across the screen.
                var skinM = palette is not null && index < palette.Length ? palette[index] : Matrix4x4.Identity;
                var m = skinM.IsIdentity ? baseM : skinM * baseM;
                if (transpose) m = Matrix4x4.Transpose(m);

                slot[0] = m.M11; slot[1] = m.M12; slot[2] = m.M13; slot[3] = m.M14;
                slot[4] = m.M21; slot[5] = m.M22; slot[6] = m.M23; slot[7] = m.M24;
                slot[8] = m.M31; slot[9] = m.M32; slot[10] = m.M33; slot[11] = m.M34;
                slot[12] = m.M41; slot[13] = m.M42; slot[14] = m.M43; slot[15] = m.M44;

                for (int i = 0; i < rows * 4; i++)
                    BitConverter.TryWriteBytes(bytes.AsSpan(at + i * 4, 4), slot[i]);
            }

            _pendingCb = bytes; _pendingLength = need;
            return;
        }

        foreach (var v in cb.Variables)
        {
            float[]? data = null;

            // M456: ENV_LIGHTING_MASK is a UINT bitmask, and the only constant here that is not a float.
            //
            // The shader ANDs it in two halves and requires BOTH to overlap the light's own 16-bit mask
            // (light-system.md §2.3): a light is drawn only when (mask & ENV & 0x00FF) != 0 AND
            // (mask & ENV & 0xFF00) != 0. Leaving this zero culls every light in complete silence - the
            // single easiest way to build this whole path correctly and see nothing at all.
            //
            // Written as raw BITS rather than through the float[] path below, which would upload the
            // floating-point value 65535.0 (bit pattern 0x477FFF00) and fail both halves.
            //
            // Deliberately NOT deferring to mat.Params: a float[] cannot express a bitmask, so a material
            // parameter of this name could only ever be wrong here. Measured: the string
            // "ENV_LIGHTING_MASK" appears ZERO times in shaders.bin, so no shader definition declares it
            // as a parameter and no material can inherit a default for it - it is engine-supplied per
            // object. An explicit Overrides entry (the Constants tab) still wins, read as an INTEGER,
            // which is the only sane reading of a typed-in value for a uint constant.
            if (v.Name.Equals("ENV_LIGHTING_MASK", StringComparison.OrdinalIgnoreCase))
            {
                uint mask = Overrides.TryGetValue(v.Name, out var om) && om.Length > 0
                    ? (uint)Math.Clamp(om[0], 0f, uint.MaxValue)
                    : Formats.Lighting.ClusterLightBuilder.EnvLightingMaskAll;
                if (v.Offset >= 0 && v.Offset + 4 <= bytes.Length)
                    BitConverter.TryWriteBytes(bytes.AsSpan(v.Offset, 4), mask);
                continue;
            }

            // a material's own authored value wins over the window's global override, which wins over
            // the engine stand-ins below
            if (mat is not null && mat.Params.TryGetValue(v.Name, out var pv)) data = pv;
            else if (Overrides.TryGetValue(v.Name, out var ov)) data = ov;
            else
            {
                data = v.Name.ToUpperInvariant() switch
                {
                    "WORLD_MATRIX" or "MWORLD" => Mat(_instanceWorld ?? world, s),   // M676: per placement
                    "VIEW_PROJECTION_MATRIX" or "MVIEWPROJ" => Mat(vp, s),
                    "MVIEW" => Mat(view, s),
                    "MVIEWINV" => Mat(Invert(view), s),
                    "MWORLDINV" => Mat(Invert(_instanceWorld ?? world), s),
                    // the ambient cube a character is lit by when it is not standing on a baked lightgrid.
                    // Neutral rather than zero: zero renders the model black and looks like a load failure.
                    "LIGHTGRID_COLORS" => new[]
                    {
                        0.5f, 0.5f, 0.5f, 1f, 0.5f, 0.5f, 0.5f, 1f, 0.5f, 0.5f, 0.5f, 1f,
                        0.5f, 0.5f, 0.5f, 1f, 0.5f, 0.5f, 0.5f, 1f, 0.5f, 0.5f, 0.5f, 1f,
                    },
                    // M231: mProj is TWO different matrices depending on who is asking, and the shader
                    // itself does not say which - only the company it keeps does.
                    //
                    // Censused every vertex stage in the cache and the split is total:
                    //   235 use mProj AND declare a bone buffer  -> champions. M646: the bone palette
                    //       carries object-to-WORLD (see BonePose), so mProj is the view-projection too;
                    //       under the old view-space pose it was projection alone.
                    //    17 use mProj and declare NO bone buffer -> and all 17 are particle shaders
                    //       (particles/* and particlesystem/*). Nothing else is in that set.
                    //
                    // For the particle case mProj must be the FULL world-to-clip transform, which
                    // particlesystem/quad_vs proves directly: it computes POSITION - vCamera, and vCamera is
                    // a world-space camera position, so POSITION is world-space and mProj has to finish the
                    // job. Binding projection alone there puts the quad on the near plane.
                    //
                    // Five staticmesh shaders (env_scrollingdiffuse, tft_*) use mProj AND a VP matrix. They
                    // are outside this rule and keep the old behaviour; nothing has measured what their
                    // mProj is for.
                    // M646: and bone shaders posed in WORLD space (the default now) need it too - the
                    // bones no longer carry the view, so mProj has to. See BonePose.
                    "MPROJ" => Mat(NeedsViewProjection(mat, s) ? vp : proj, s),

                    // Engine/map-owned transforms. The shader definition cannot author these because
                    // their real values come from the loaded map. Identity is the neutral bench value:
                    // it preserves UVs instead of collapsing every lookup onto texel (0,0).
                    "NAV_GRID_XFORM" or "BAKED_PAINT_UV_SCALE_BIAS" or "TERRAIN_XFORM"
                        => new[] { 1f, 1f, 0f, 0f },

                    // The standalone preview has no terrain depth raster or Teemo gameplay overlay.
                    // Zero is the correct disabled value, but bind it explicitly so it is not reported as
                    // a missing input and cannot hide a genuinely destructive unbound constant.
                    "TEEMO_ACTIVE" => new[] { 0f, 0f, 0f, 0f },

                    // M465: the two depth biases, in the form the shader applies them (light-system.md §1.7,
                    // re-verified at DefaultEnv_Flat blob 226 lines 172-177):
                    //     bias = SLOPE_SCALED * (1 - dot(Ngeo, normalize(SUN_LIGHT_DIRECTION))) + CONSTANT
                    //     ref  = saturate(shadowCoord.z - bias)
                    // Ngeo is the GEOMETRIC normal, rebuilt per pixel from ddx/ddy of the world position
                    // (lines 163-171), so it follows the triangle rather than the shading normal - which is
                    // what makes the slope term track the actual depth gradient across a texel.
                    //
                    // Both are in NORMALISED depth units, so both are derived from the frustum fit rather
                    // than being constants: SunShadowFit expresses them in shadow texels of world size and
                    // divides by the fitted depth range. Zero when no fit exists, which is what they always
                    // were - and zero bias with the white stand-in is still fully lit.
                    "CONSTANT_DEPTH_BIAS" => new[] { _shadowFrame?.ConstantDepthBias ?? 0f, 0f, 0f, 0f },
                    "SLOPE_SCALED_DEPTH_BIAS" => new[] { _shadowFrame?.SlopeScaledDepthBias ?? 0f, 0f, 0f, 0f },

                    // M456: the world->cluster affine map and its clamp, for Riot's own light loop.
                    //
                    // NOT run through Mat(): the shader consumes these as three CONSTANT REGISTERS dotted
                    // with float4(worldPos, 1) - Mantis blob 27 lines 636-638, DefaultEnv_Flat blob 226
                    // lines 208-210 - so the float order is fixed by the disassembly and the row/column
                    // -major transpose toggle must not touch it. ClusterLightGrid.WorldToCluster is
                    // already in that order.
                    //
                    // The identity default is a ZERO scale with w = 1, which maps every pixel to cell 0.
                    // Cell 0 of an unbuilt grid points at the all-zero header, so an unbuilt or failed
                    // grid means "no lights here" rather than an out-of-range read.
                    "WORLD_TO_CLUSTER_TRANSFORM" => _clusterXform,
                    "CLUSTER_MAX_CLAMP" => _clusterMaxClamp,

                    // A one-texel neutral light-region texture has size 1x1. Zero makes reciprocal-size
                    // arithmetic non-finite in map PBR shaders even when the stand-in texture is white.
                    "LIGHT_REGION_TEXTURE_SIZE" => new[] { 1f, 1f, 0f, 0f },

                    // The engine normally fills one scale per IBL cube. All-zero scales erase the entire
                    // indirect-light contribution, which leaves PBR materials black despite valid albedo.
                    //
                    // M463: the map's skyLightScale when it authored one - this is the STRENGTH half of the
                    // sky, the hue half being the cube stand-in (UpdateSkyAmbient). The shader reads only
                    // .x of the indexed element (blob 27 lines 495/499), but all 32 slots are filled so the
                    // value does not depend on which probe index a record happens to carry.
                    "IBL_CUBEMAP_SCALES" => SkyAmbientScale is { } sky
                        ? Repeat(new[] { sky, sky, sky, sky }, 32)
                        : NeutralIblCubemapScales,

                    // M256/M465: the shadow plumbing. LIVE when this frame rendered a shadow map, and the
                    // M256 placeholders otherwise - a scene with no sun, no caster on screen or no shadow
                    // blobs still has to bind something finite here, because an unbound constant is the
                    // signal this project uses to find real bugs (M229, M230, M235, M255 were all found that
                    // way) and a permanent false positive is worse than none.
                    //
                    // The placeholders are what they always were, and are safe for the same reason: with no
                    // fit, _shadowFrame is null, StandIn hands back the 1x1 white depth texture and the
                    // comparison sampler stays on Always, so every PCF tap returns 1 whatever these hold.
                    //
                    // mShadowProj maps world into shadow-map space - (u, v, depth), with the NDC-to-texture
                    // remap already folded in, because the consuming vertex shader takes rows 0-2 and uses
                    // the result directly (defaultenv_flat.vs blob 13 lines 107-110: three dp4s against
                    // cb2[11..13], no divide by w). Identity is the only defensible stand-in without a
                    // shadow camera; a zero matrix would collapse every tap onto one texel.
                    "MSHADOWPROJ" => Mat(_shadowFrame?.ShadowProj ?? Matrix4x4.Identity, s),

                    // Pushes the shadow lookup along the normal before projecting it, which trades acne for
                    // a little contact-shadow shrinkage. Applied in the VERTEX shader, not the pixel shader -
                    // defaultenv_flat.vs lines 103-106 do
                    //     world += N * ((1 - abs(dot(N, SUN_LIGHT_DIRECTION))) * NORMAL_OFFSET_BIAS)
                    // so it reaches its maximum on surfaces edge-on to the sun, exactly like the slope-scaled
                    // depth bias, and is in WORLD units rather than depth units.
                    //
                    // Left at zero even with a live fit. It is a third bias knob layered on the two the
                    // pixel shader already applies, its useful magnitude depends on the map's scale, and
                    // tuning three interacting biases against a frame nobody has looked at yet would be
                    // guessing three times instead of once. Wired here so it is one edit away.
                    "NORMAL_OFFSET_BIAS" => new[] { 0f, 0f, 0f, 0f },

                    // The 5-tap PCF kernel, as two UV offset pairs: centre, uv+O1, uv-O1, uv-O2, uv+O2.
                    // Derived from the shadow map's actual size rather than a hardcoded 2048 - see
                    // SunShadowFit.SampleOffsets, which also records that only .xy/.zw are offsets and that
                    // light-system.md §4.3's "(du, dv, dz)" is wrong for this constant.
                    "SHADOW_SAMPLE_OFFSETS" => _shadowFrame is { } sf
                        ? new[] { sf.SampleOffsets.X, sf.SampleOffsets.Y, sf.SampleOffsets.Z, sf.SampleOffsets.W }
                        : new[] { 1f / 2048f, 1f / 2048f, -1f / 2048f, 1f / 2048f },
                    "VCAMERA" or "CAMERA_POSITION" => new[] { cam.X, cam.Y, cam.Z, 1f },

                    // M231: the particle flipbook atlas descriptor. Derived from quad_vs, which spends it as
                    //     col = frame - floor(frame * TEXTURE_INFO.y) * TEXTURE_INFO.x
                    //     out.uv = (col + u) * TEXTURE_INFO.y, (row + v) * TEXTURE_INFO.z
                    // so x = columns, y = 1/columns, z = 1/rows. A single-frame particle is a 1x1 atlas,
                    // which passes the UV through unchanged - the right default for a still preview, and the
                    // only one that does not silently crop the texture to a sub-rectangle.
                    "TEXTURE_INFO" => new[] { 1f, 1f, 1f, 0f },

                    // M231. Shifts the quad along the view ray to bias it in the depth test; zero is
                    // "where the emitter put it". NOTE: this case was accidentally deleted by the M234
                    // patch and restored here - the unbound-constant report is what caught it.
                    "PARTICLE_DEPTH_PUSH_PULL" or "EMITTER_DEPTH_PUSH_PULL" or "CAMERAOFFSET"
                        => new[] { 0f, 0f, 0f, 0f },

                    // M234: DERIVED, by disassembling the permutation each define selects. The earlier
                    // attempt reasoned from field names, made two permutations worse and none better, and
                    // was reverted; these are read off the arithmetic instead, and each is chosen so the
                    // stage is a provable no-op for ALL inputs rather than merely plausible.

                    // ALPHA_EROSION, from ps blob 15:
                    //     dp4_sat r0.y, erosionTexel, cb0[1]     // e = sat(dot(texel, MIXER))
                    //     add     r0.x, r0.y, -cb0[0].y
                    //     add     r0.xy, -r0.xyxx, v3.zzzz       // x = drive-e+P.y , y = drive-e
                    //     mul_sat r0.xy, r0.xyxx, cb0[0].zwzz    // x *= P.z , y *= P.w
                    //     add     r0.x, -r0.y, r0.x              // mask = sat(x) - sat(y)
                    // With drive and e both in [0,1], drive-e is in [-1,1]. P.y = 2 puts x in [1,3] so
                    // sat(x) is 1 everywhere, and P.w = 0 forces sat(y) = 0, giving mask = 1 for every
                    // input. P.y = 1 was the earlier guess and it fails exactly when the erosion map is
                    // the WHITE stand-in: e = 1, drive = 0, sat((0-1+1)*1) = 0, sprite fully erased. That
                    // is why those permutations rendered blank. P.x is not referenced at all here.
                    "CALPHAEROSIONPARAMS" => new[] { 0f, 2f, 1f, 0f },
                    // Dotted against the erosion texel; the census measured (1,0,0,0) - red - on ~74%.
                    "CALPHAEROSIONTEXTUREMIXER" => new[] { 1f, 0f, 0f, 0f },

                    // ALPHA_TEST, from ps blob 8:
                    //     mad r1.x, v1.w, r0.w, -cb0[0].x   // vColor.a * tex.a - REF
                    //     lt  r1.x, r1.x, l(0)
                    //     discard_nz r1.x
                    // so a reference of 0 discards only where alpha is negative, i.e. never.
                    "ALPHATESTREFERENCEVALUE" => new[] { 0f, 0f, 0f, 0f },

                    // PALETTIZE_TEXTURES, from ps blob 21:
                    //     dp4_sat r1.x, spriteTexel, cb0[1]   // u = sat(dot(texel, SRC_MIXER))
                    //     add     r1.x, r1.x, cb0[0].z        // u += Select.z
                    //     add     r1.y, cb0[0].w, cb0[0].x    // v  = Select.w + Select.x
                    //     sample  paletteStrip(u, v)
                    // There is no bypass - the palette colour REPLACES the sprite's - so this stage cannot
                    // be neutralised, only fed sanely: row 0, and the red channel driving the lookup, which
                    // is the same convention the erosion mixer uses. A real emitter overrides both.
                    "CPALETTESELECTMAIN" => new[] { 0f, 0f, 0f, 0f },
                    "CPALETTESRCMIXERMAIN" => new[] { 1f, 0f, 0f, 0f },

                    // SOFT_PARTICLES, from ps blob 129. Both values are derived and explained in
                    // ParticleShading, which is also what the tests assert them through - a wrong value
                    // here does not misdraw the particle, it erases it, and neither failure is legible on
                    // screen. The params are the NEUTRAL band (fade = 1 at every depth) for a material
                    // whose emitter authored no widths; a real emitter's own values arrive through
                    // mat.Params, which is consulted before this switch.
                    "CSOFTPARTICLEPARAMS" => Vec(ParticleShading.NeutralSoftParams),
                    "CSOFTPARTICLECONTROL" => Vec(mat.ParticleSoftControl ?? ParticleShading.SoftControl(mat is { Additive: true })),
                    // M363: derived from the live projection now, rather than the neutral placeholder that
                    // stood here while nothing sampled depth. Feeds two reciprocals, so DepthConversionFrom
                    // falls back to that same placeholder rather than ever returning a zero component - a
                    // zero makes d NaN and the quad vanishes.
                    "CDEPTHCONVERSIONPARAMS" => DepthConversionFrom(proj),

                    // M232: MULT_PASS's second flipbook atlas descriptor, same shape as TEXTURE_INFO
                    // and derived the same way (M231).
                    "TEXTURE_INFO_2" => new[] { 1f, 1f, 1f, 0f },
                    "KCOLORFACTOR" => new[] { 1f, 1f, 1f, 1f },

                    // M262: an additive bias on OUTPUT ALPHA. The identity for an add is zero.
                    // From staticmesh/env_glowsign ps blob 0, $Globals+16 -> cb0[1].x:
                    //
                    //     add o0.w, r2.w, cb0[1].x        // outAlpha = alpha + Alpha_Offset
                    //
                    // Declared by exactly four pixel shaders - skinnedmesh/diffuse_alpha_add,
                    // skinnedmesh/glowsign, staticmesh/env_glowsign and env_glowsign_atlas - USED in all
                    // 2,176 permutations, and carrying no RDEF default in any of them. M257 called it
                    // unresolvable after finding nothing in shaders.bin; it is simply a per-material
                    // parameter, authored by 26 bins with values from -0.795 to 2. A spread straddling
                    // zero is what a bias around a zero default looks like, and half of Map12's eight
                    // ENV_GlowSign materials omit it entirely - so Riot's own content depends on the
                    // default being neutral.
                    //
                    // Zero was already the effective value, because an unwritten constant reads as zero.
                    // Binding it makes that deliberate rather than incidental and clears the last standing
                    // entry out of the unbound report - a permanent false positive is worse than none,
                    // since it is the instrument that solved M229, M230, M235, M255 and M261.
                    // Material-authored values still win: mat.Params is consulted before this switch.
                    "ALPHA_OFFSET" => new[] { 0f, 0f, 0f, 0f },
                    "TIME" => new[] { s.TimeSeconds, s.TimeSeconds * 0.5f, MathF.Sin(s.TimeSeconds), 1f },
                    // M228: this points TOWARD the sun, and getting it backwards makes flat ground black.
                    //
                    // From staticmesh/defaultenv_flat ps blob 59:
                    //     dp3 r0.x, normal, cb1[7].yzw      // N . SUN_LIGHT_DIRECTION
                    //     max r0.x, r0.x, l(0.000000)
                    //     mad r0.xyz, r0.xxxx, occl*SUN_COLOR, baked*SCALE
                    //
                    // So a direction pointing DOWN - which is what the UI slider naturally produces, and what
                    // the comparison shader wants - gives max(negative, 0) = 0 on every up-facing surface.
                    // The whole sun term vanishes and the only light left is the baked one, whose atlases
                    // measure a mean of 6.5-50 out of 255 on Map12. That reads as black ground under lit
                    // walls, which is exactly what was reported. MapSunProperties.SunDirection defaults to
                    // (0,1,0) - up - which independently says the stored convention is toward-the-sun.
                    // M275: NORMALISED, because Riot's own shaders never do it and their artists never
                    // authored it. The dp3 above consumes this constant raw, so a non-unit vector scales
                    // the whole sun term by its LENGTH - and Riot ships lengths up to 8.775
                    // (Map22 base_dragon_cloud <2, 8, -3>). Every one of the 256 distinct pixel
                    // permutations of DefaultEnv_Flat / VertexDeform / Env_GlowSign uses
                    // SUN_LIGHT_DIRECTION and not one normalises it, so the client must be doing it before
                    // upload or Riot's own TFT maps would render 7-9x over-lit.
                    //
                    // The authoring tells agree: on Map22 the GLOBAL sun blocks are unit-length 136 times
                    // of 156 - darkstar_supernova's is <-0.38480574, 0.8271427, -0.40958446>, normalised to
                    // seven decimals - while the lighting VOLUME blocks in the SAME bins are non-unit 126
                    // times of 159 and read as hand-typed (<-0.8, 6.82, 2>). Two tools, one habit applied
                    // in only one of them. Intensity also already has its own field (sunColor), so length
                    // cannot be carrying it too.
                    //
                    // Measured, lightmapped slices only, mean luma of lit pixels:
                    //   Map22/darkstar_supernova (len 7.152)  191.5/255 with 31.7% blown to white -> 58.9 with 0.0%
                    //   Map22/base_dragon_fire   (len 7.152)  126.3 -> 37.0
                    //   Map30/arenab             (len 6.453)  217.6 with 3.5% blown -> 90.8 with 0.0%
                    //   Map12/bloom              (len 0.9996) 107.6 -> 107.6, bit-for-bit no-op
                    //   Map11/base_srx           (len 0.792)  112.4 -> 112.4, and 112.4 with the sun term
                    //                                         REMOVED - SR's visible permutations do not
                    //                                         read the sun at all, so this cannot touch it
                    //
                    // Not done instead: dropping the sun on lightmapped surfaces (the intuitive fix, since
                    // a bake already contains the sun). Riot's shader ADDS them - it samples BAKED_LIGHT,
                    // multiplies rgb by LIGHT_MAP_COLOR_SCALE, and mads the sun on top gated by the
                    // lightmap's ALPHA, which carries the sun's SHADOW rather than replacing its light.
                    // Removing it costs Map12/bloom 61% of its brightness (107.6 -> 41.7).
                    //
                    // The parsed MapSunProperties are deliberately left alone so the Map Bin editor keeps
                    // showing what Riot actually wrote.
                    "SUN_LIGHT_DIRECTION" => s.MapSunDirection is { } msd
                        ? UnitSun(msd)
                        : new[] { -s.SunDirection.X, -s.SunDirection.Y, -s.SunDirection.Z, 0f },

                    "SUN_LIGHT_COLOR" => s.MapSunColor is { } msc
                        ? new[] { msc.X, msc.Y, msc.Z, msc.W }
                        : new[] { s.SunColor.X, s.SunColor.Y, s.SunColor.Z, s.SunColor.W },
                    // M223: the fog-of-war neutral is NOT all zeros, which is what these were.
                    //
                    // From the vertex shader (staticmesh/defaultenv_flat):
                    //     mad o3.xy, r0.xzxx, cb2[19].xyxx, cb2[19].zwzz   // uv   = worldXZ * FOW_PARAMS.xy + .zw
                    //     mad_sat o3.z, r0.y, cb2[21].x, cb2[21].y         // fade = saturate(worldY * HEIGHT_FADE.x + .y)
                    // and the pixel shader:
                    //     blend = fade * (1 - fowMap.a) + fowMap.a
                    //     rgb   = lerp(fowRgb, lit, blend)
                    //
                    // With HEIGHT_FADE all zero the fade term is saturate(0) = 0, so anything the FOW map
                    // does not mark fully visible collapses toward the fog colour, and it does so as a
                    // function of WORLD Y - which is exactly the reported "meshes below a certain height go
                    // black". Setting .y = 1 makes the fade saturate to 1 at every height, so the geometry
                    // stays lit no matter what the FOW map says. That is the honest neutral for a preview
                    // with no fog of war, and it is read off the shader rather than guessed.
                    "FOW_HEIGHT_FADE" => new[] { 0f, 1f, 0f, 0f },

                    // uv = worldXZ * 0 + 0 samples one texel of the (white) stand-in, which is what a
                    // fully-revealed map looks like. Left at zero deliberately.
                    "FOG_OF_WAR_PARAMS" => new[] { 0f, 0f, 0f, 0f },

                    // M229: DEPTH FOG, and all-zeros here is what turned every mesh below world Y = 0
                    // completely black. From staticmesh/defaultenv_flat ps blob 152 - the base permutation,
                    // which is what an ordinary map material resolves to:
                    //
                    //     add     r0.w, -cb1[10].y, cb1[10].x     // start - end
                    //     div     r0.w, l(1.0), r0.w              // 1 / (start - end)
                    //     add     r1.x, v1.w, -cb1[10].y          // v1.w is WORLD Y, from the VS
                    //     mul_sat r0.w, r0.w, r1.x                // t = saturate((worldY - end)/(start - end))
                    //     ... smoothstep(t), * 2.88539, exp2, reciprocal -> fogFactor
                    //     mad o0.xyz, fogFactor, (fogColour - lit), lit
                    //
                    // With start = end = 0 the divide is 1/0 = INF, so t becomes a STEP at worldY = 0:
                    // 1 above it and 0 below. That makes fogFactor 0.135 above and exactly 1.0 below, and
                    // a fogFactor of 1 replaces the pixel with the fog colour outright - which was black,
                    // because nothing supplied ENV_FOG_COLOR either. Hence "meshes under a specific y value
                    // go fully black", and only partly on a mesh that straddles zero.
                    //
                    // Riot stores fogStartAndEnd negative and "reversed" (Twisted Treeline ships
                    // -10000, -50000). The shader consumes them raw, so they are passed through raw rather
                    // than through TryGetFogRange's (near, far) normalisation.
                    "ENV_FOG_START_END_SCALE_EMISSIVE_REMAP" => s.MapFogStartEnd is { } fse
                        ? new[] { fse.X, fse.Y, 1f, 1f }
                        // No map fog: pick a range wide enough that t saturates to 1 everywhere, so the
                        // factor is the uniform 0.135 minimum and there is no cliff. The stage cannot be
                        // switched off from the constants - 0.135 is the floor of 1/exp2(smoothstep*2.885).
                        : new[] { 1f, -1e9f, 1f, 1f },

                    "ENV_FOG_COLOR" or "ENV_FOG_ALT_COLOR" => s.MapFogColor is { } fc
                        ? new[] { fc.X, fc.Y, fc.Z, fc.W }
                        : new[] { 0f, 0f, 0f, 1f },

                    // M395: the environment-transition crossfade. Riot's shader already does
                    //     lerp(GRASS_TINT_MAP, GRASS_TINT_MAP_ALTERNATE, GRASS_INTERP)
                    // so this constant IS the transition here. UPPERCASE on purpose - the switch subject
                    // is v.Name.ToUpperInvariant(), which is why the mixed-case arms further down
                    // ("GrassDistortSpheres", "GrassVelocities", "VelocityStrength") are unreachable.
                    "GRASS_INTERP" => new[] { s.GrassInterp, 0f, 0f, 0f },

                    // Below this world height the engine treats everything as permanently visible. Nothing
                    // in the preview should ever be force-fogged, so push it above any real geometry.
                    "FOG_OF_WAR_ALWAYS_BELOW_Y" => new[] { 1e9f, 1e9f, 1e9f, 1e9f },

                    // M230: the ten grass-flattening spheres - one per nearby unit, the reason grass parts as
                    // a champion walks through it. Leaving them zero did not merely disable the effect, it
                    // made grass VANISH. staticmesh/vertexdeform vs blob 25:
                    //
                    //     add  r7.xyz, -r3.xyzx, r7.xyzx    // sphereXZ - pivotXZ, both (0,0,0)
                    //     dp3  r1.w, r7.yzxy, r7.yzxy       // 0
                    //     rsq  r2.w, r1.w                   // rsq(0) = +INF
                    //     mul  r7.xyz, r2.wwww, r7.xyzx     // INF * 0 = NaN
                    //     ...
                    //     add  r6.xyz, r6.xyzx, r7.xyzx     // NaN accumulates over all ten iterations
                    //     add  r1.xyz, r1.xyzx, r6.xyzx     // and lands in the output POSITION
                    //
                    // A NaN vertex position makes the rasteriser discard the triangle, so all 104,876
                    // triangles of Map12 grass drew nothing at all.
                    //
                    // The inert state is "no unit is standing in the grass": spheres far away, radius zero.
                    // Both loops then resolve to exactly no effect rather than to NaN -
                    //   distortion: len is huge and (spread*radius*velocity) is 0, so div_sat gives 1, the
                    //     angle works out to (1*-0.1 + 0.1)*2pi = 0, and sincos(0) is the identity rotation;
                    //   see-through alpha: t = saturate((dist - R)/R) reaches 1 well before 2R, and the final
                    //     lerp(SeeThroughAlphaMin, SeeThroughAlphaMax, 1) is fully opaque.
                    // 1e6 is ~60x outside any real map yet squares to 3e12, far inside fp32 range.
                    "GrassDistortSpheres" => Repeat(new[] { 1e6f, 1e6f, 1e6f, 0f }, 10),
                    "GrassVelocities" => Repeat(new[] { 0f, 0f, 0f, 0f }, 10),

                    // Scales the velocity term above. Neutral at 1; with zero velocities it is moot either way.
                    "VelocityStrength" => new[] { 1f, 1f, 1f, 1f },

                    // The mesh's own centre, and NOT only a distance reference: it is also the wave's phase
                    // offset, sin(sin(cx+cy+cz) + WaveFrequency*TIME), which is how Riot stops every clump on
                    // the map from swaying in lockstep. The scene loader overrides this per material slice
                    // via Params; this fallback is for a single-mesh preview, already centred on the origin.
                    "MESH_CENTER" => new[] { 0f, 0f, 0f, 0f },

                    // These two ADD, and the sum multiplies the diffuse. The sum is the only observable
                    // quantity here (DISABLE_SHADOWS forces the shadow term), which is why the split below
                    // is a labelled stand-in: four splits summing to 1.0 measured identically, a sum of 2.0
                    // doubled, a sum of 0 went black.
                    //
                    // CORRECTION (M366): M212's headline formula, "output = saturate(2 x texture) x ...",
                    // was WRONG, and the overbright-albedo story built on top of it was wrong with it.
                    // Disassembling staticmesh/defaultenv_flat blob 53 gives
                    //     lt r1.xyz, r1.xyzx, l(0.5)
                    // - the branch tests the SAMPLED TEXEL, so the operator is Overlay(base = texture,
                    // blend = TintColor), not a doubling of anything. M212's sweep bound TintColor = 1.0,
                    // where Overlay's lower branch degenerates to 2D and the upper collapses to exactly
                    // 1.0; that is the "1.00x from 64 to 126, then clamped hard at 127" it recorded, and it
                    // is a harness artefact rather than a property of the shader. At the value Riot
                    // actually ships - TintColor 0.5019608 on 282 of 301 DefaultEnv_Flat materials in
                    // milkshake_srs, 0.5 on the other 19 - Overlay evaluates to 1.004x. SRX_DynamicEffect's
                    // hard-light variant at tint 0.5 is exactly 1.000x.
                    //
                    // So there is NO overbright convention in these shaders to preserve or cancel, and the
                    // sum of 1.0 is not justified by one. It is justified only by being neutral for a term
                    // that multiplies. What the game really uses is NOT RECOVERABLE from shipped data:
                    // scanning all 2,883 bins in Map11.wad.client for shadowColor / ScaleSunShadowIntensity
                    // / colorblindShadowColor returns ZERO hits (engine-side, not map data), and across all
                    // 829 shipped PS TOCs SHADOW_COLOR never co-occurs with a baked, sun, env or HDR term,
                    // so no shipped shader reveals its magnitude either.
                    //
                    // Do not "fix" map brightness by raising this. It is one PerFramePixelCB pair shared by
                    // every material on the map (336 of 336 on milkshake_srs consume it identically), so it
                    // can only ever rescale the WHOLE image - it cannot make one shader darker than
                    // another, and a sum of 2.0 specifically is the M210 defect that clipped everything
                    // bright to white. Both are editable in the Constants tab.
                    "SHADOW_COLOR" => new[] { 0.35f, 0.35f, 0.35f, 1f },
                    "SHADOW_COLOR_COMPLEMENT" => new[] { 0.65f, 0.65f, 0.65f, 1f },

                    // Not USED by the permutation the above was measured on (it carries NO_BAKED_LIGHTING),
                    // so this is unverified and stays neutral rather than being set to the 2 that map data
                    // records for lightMapColorScale. Doubling on an untested guess is the exact mistake
                    // above.
                    // M228: a SCALAR at PerFramePixelCB+128 (ENV_FOG_COLOR sits at +132, so the float4
                    // stand-in was only ever safe because FillConstantBuffer clamps to v.Size/4 = 1 float).
                    // The shader does baked.rgb * this. Map data records 2 for Map12 and 0.6 for Map11, so
                    // it is per map and must come FROM the map rather than be hardcoded either way.
                    "LIGHT_MAP_COLOR_SCALE_AND_INTENSITY" => s.MapLightMapScale is { } lms
                        ? new[] { lms, lms, lms, lms }
                        : new[] { 1f, 1f, 1f, 1f },

                    // M224: the lightmap UV transform, and the single biggest cause of black map ground -
                    // 899 materials on Map12 read it and nothing supplied it, so it uploaded as zero and
                    // collapsed every lightmap lookup onto texel (0,0) of the atlas.
                    //
                    // Identity rather than the map's real values, because MapGeoDecoder has ALREADY applied
                    // the per-mesh scale and bias when it produced the lightmap UVs (uv7 * scale + bias).
                    // Applying it a second time here would be the error this replaces, in the other
                    // direction.
                    "BAKED_LIGHT_SCALE_AND_BIAS" => new[] { 1f, 1f, 0f, 0f },
                    // M284: "Tint" as well as "TINTCOLOR". The unbound report only names constants the
                    // shader actually USES (FillConstantBuffer gates on v.IsUsed), and $Globals.Tint was on
                    // that list for Map453's map geometry - so this was a used float4 multiplier arriving
                    // as zero, exactly the failure the M218 note below describes.
                    "TINTCOLOR" => new[] { 1f, 1f, 1f, 1f },

                    // M218: constants that MULTIPLY must not default to zero.
                    //
                    // Everything unrecognised falls through to a zero-filled buffer, which is the right
                    // conservative choice for an additive or offset term and catastrophically wrong for a
                    // multiplicative one. `Alpha` is a champion material's opacity: with no value it came
                    // through as 0 and Kayn rendered perfectly and completely invisibly - the geometry, the
                    // bones and the textures were all correct and every pixel was discarded at the end.
                    //
                    // Measured, not assumed: sweeping every USED constant of skinnedmesh/diffuse_alpha over
                    // the loaded model, `Alpha` was the ONLY one that changed whether anything appeared
                    // (0 lit pixels at 0, 1,684 at 1). The others below are not proven the same way - they
                    // are scale terms where 1 is the identity and 0 would silently erase their contribution,
                    // so 1 is the honest neutral rather than a measurement.
                    "ALPHA" => new[] { 1f, 1f, 1f, 1f },
                    "LIGHTGRID_SCALE" or "KGRASSFADE" => new[] { 1f, 1f, 1f, 1f },
                    _ => null,
                };
            }

            data ??= v.DefaultValue;
            if (data is null)
            {
                if (v.IsUsed) unbound?.Add($"{cb.Name}.{v.Name} ({v.TypeName})");
                continue;                                        // leaves zeros
            }

            int n = Math.Min(v.Size / 4, data.Length);
            for (int i = 0; i < n; i++)
            {
                int at = v.Offset + i * 4;
                if (at + 4 > bytes.Length) break;
                BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), data[i]);
            }
        }

        _pendingCb = bytes;
        _pendingLength = need;
    }

    private byte[] _pendingCb = Array.Empty<byte>();
    private byte[] _cbScratch = new byte[1024];
    private byte[] _readback = Array.Empty<byte>();
    private int _pendingLength;
    private ComPtr<ID3D11Buffer> _compareCb;

    private void EnsureCompareCb()
    {
        if (_compareCb.Handle is not null) return;
        var d = new BufferDesc
        {
            ByteWidth = 32, Usage = Usage.Dynamic,
            BindFlags = (uint)BindFlag.ConstantBuffer, CPUAccessFlags = (uint)CpuAccessFlag.Write,
        };
        ComPtr<ID3D11Buffer> b = default;
        _device.CreateBuffer(in d, null, ref b);
        _compareCb = b;
    }

    /// <summary>M461: a Vector4 as the float[] the constant feed uploads.</summary>
    private static float[] Vec(Vector4 v) => new[] { v.X, v.Y, v.Z, v.W };

    private static float[] Mat(Matrix4x4 m, PreviewSettings s)
    {
        if (s.TransposeMatrices) m = Matrix4x4.Transpose(m);
        return new[]
        {
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44,
        };
    }

    /// <summary>M245: how many slices the frustum rejected last frame, next to DrawCalls. Reported rather
    /// than assumed - a culler that quietly rejects nothing is indistinguishable from one that works.</summary>
    public int CulledSlices { get; private set; }

    /// <summary>M623: how many of the scene's own materials actually issued a DrawIndexed last frame.
    ///
    /// <para>Separate from <see cref="DrawCalls"/>, which also counts gizmo, icon, grid and light draws.
    /// That mixing is what made "2 draw(s)" ambiguous on a 5-submesh character: two materials drawing, or
    /// none drawing and two overlays. Those are completely different faults.</para></summary>
    public int GeometryDraws { get; private set; }

    /// <summary>M623: materials the draw loop skipped because Visible was false.</summary>
    public int HiddenSlices { get; private set; }

    /// <summary>M246: how many times the pipeline actually changed while drawing the last frame. With
    /// sorting on this approaches the number of distinct pipelines; without it, it approaches the number
    /// of draws. Reported so the difference is visible rather than claimed.</summary>
    public int PipelineSwitches { get; private set; }

    private readonly List<int> _drawOrder = new();

    /// <summary>Gribb-Hartmann plane extraction from a combined view-projection, in System.Numerics'
    /// row-vector convention (v * M). Planes point INWARD; a point is inside when every dot is >= 0.</summary>
    private static Vector4[] ExtractFrustum(Matrix4x4 m)
    {
        var p = new Vector4[6];
        p[0] = new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41);  // left
        p[1] = new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41);  // right
        p[2] = new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42);  // bottom
        p[3] = new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42);  // top
        // Near uses the UNSUMMED row because D3D clip space is 0..1 in z, not -1..1. Using the OpenGL form
        // here would put the near plane in the wrong place and cull geometry in front of the camera.
        p[4] = new Vector4(m.M13, m.M23, m.M33, m.M43);                                   // near
        p[5] = new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43);   // far
        for (int i = 0; i < 6; i++)
        {
            float len = new Vector3(p[i].X, p[i].Y, p[i].Z).Length();
            if (len > 1e-6f) p[i] /= len;
        }
        return p;
    }

    /// <summary>True when the box is not entirely outside any single plane. This is the cheap
    /// conservative test: it can keep a box the frustum does not really touch, which costs a draw, but it
    /// never rejects one that is visible, which would cost a hole in the image.</summary>
    private static bool FrustumContains(Vector4[] planes, Vector3 min, Vector3 max)
    {
        foreach (var pl in planes)
        {
            // the box corner furthest along the plane normal - if even that is behind, all eight are
            var far = new Vector3(
                pl.X >= 0 ? max.X : min.X,
                pl.Y >= 0 ? max.Y : min.Y,
                pl.Z >= 0 ? max.Z : min.Z);
            if (pl.X * far.X + pl.Y * far.Y + pl.Z * far.Z + pl.W < 0f) return false;
        }
        return true;
    }

    /// <summary>M245: the cull test, exposed so it can be checked against a brute-force reference without
    /// a device. Correctness here is not optional - a false reject is a hole in the image.</summary>
    public static bool TestFrustumForTests(Matrix4x4 viewProj, Vector3 min, Vector3 max)
        => FrustumContains(ExtractFrustum(viewProj), min, max);

    private static Matrix4x4 Invert(Matrix4x4 m) => Matrix4x4.Invert(m, out var r) ? r : Matrix4x4.Identity;

    /// <summary>The frame's mesh radius, which scales the headless orbit camera. Set per frame.</summary>
    private float _frameRadius = 1f;

    /// <summary>
    /// M646: the camera position as the SHADERS must see it - in the space of the positions they see.
    ///
    /// <para>Two corrections to the raw camera. The X mirror: MirrorX is applied as <c>Scale(-1,1,1) * view</c>,
    /// so every position the shader receives is League-space (unmirrored) and only the view flips it for
    /// display; the editor's camera lives on the DISPLAY side of that flip, so its League-space position is
    /// the X-mirror of it. And the headless orbit: the LookAt is built from <c>CameraPosition(s) * radius</c>,
    /// so the position the shaders get must be scaled the same way, or a fresnel evaluated from a camera
    /// one unit from the origin reads every surface as edge-on.</para>
    /// </summary>
    public Vector3 ShaderCamera(PreviewSettings s)
    {
        var c = s.SuppliedCameraPosition ?? CameraPosition(s) * _frameRadius;
        return s.MirrorX ? new Vector3(-c.X, c.Y, c.Z) : c;
    }

    /// <summary>M231: unit vector from the origin toward the camera - which is what a billboard at the origin
    /// needs to face. Uses the supplied camera position when a scene set one, otherwise the orbit.</summary>
    public static Vector3 CameraForward(PreviewSettings s)
    {
        var c = s.SuppliedCameraPosition ?? CameraPosition(s);
        return c.LengthSquared() > 1e-8f ? Vector3.Normalize(c) : Vector3.UnitZ;
    }

    private static Vector3 CameraPosition(PreviewSettings s) => new(
        s.Distance * MathF.Cos(s.Pitch) * MathF.Sin(s.Yaw),
        s.Distance * MathF.Sin(s.Pitch),
        s.Distance * MathF.Cos(s.Pitch) * MathF.Cos(s.Yaw));

    private void Upload(ComPtr<ID3D11Buffer> buf, byte[] data, int length)
    {
        MappedSubresource m = default;
        if (_ctx.Map(buf, 0, Map.WriteDiscard, 0, ref m) < 0) return;
        fixed (byte* src = data)
            System.Buffer.MemoryCopy(src, m.PData, length, length);
        _ctx.Unmap(buf, 0);
    }

    // ---------------------------------------------------------------- render

    /// <summary>
    /// <para>Draw one frame and return it as BGRA8 bytes, row-packed at <paramref name="width"/>*4.
    /// Returns null when there is nothing to draw; <paramref name="error"/> then says why.</para>
    ///
    /// <para><b>The returned array is REUSED between calls</b> - it is the renderer's staging readback
    /// buffer, not a fresh allocation. Anything that needs to hold a frame across another RenderFrame must
    /// copy it. Comparing two "different" frames without copying compares one buffer with itself, which
    /// reads as a perfect zero difference and is indistinguishable from a real engine failure; that cost a
    /// wrong diagnosis in M261. Kept reused rather than allocated because the viewport calls this every
    /// frame at up to 7 MB a time.</para>
    /// </summary>
    public byte[]? RenderFrame(int width, int height, PreviewSettings s, out string? error,
        List<string>? unboundConstants = null)
    {
        error = null;
        DrawCalls = 0;
        if (!IsReady) { error = "no shader loaded"; return null; }
        // M264: either source is enough. A particle-only frame has no static mesh, and a map frame has
        // no dynamic one until something uploads quads. M640: mesh-particle geometry counts too - a
        // system whose quads have all died while a mesh emitter still lives was refused as "no mesh set".
        if ((_vb.Handle is null || _indexCount == 0) && (_dynVb.Handle is null || _dynIndexCount == 0)
            && _meshGeoms.All(g => g is null) && _riotMeshGeoms.All(g => g is null))
        { error = "no mesh set"; return null; }
        if (width <= 0 || height <= 0) { error = "zero-sized target"; return null; }

        var sw = Stopwatch.StartNew();
        try
        {
            _sharedUploadedThisFrame.Clear();
            EnsureTargets(width, height);
            UpdateStates(s);

            // M456: Riot's own in-shader light loop needs its cluster map, data stream and transform
            // ready BEFORE the first material binds a constant buffer - FillConstantBuffer reads
            // _clusterXform / _clusterMaxClamp by name. Rebuilds only when the lights or the grid bounds
            // actually changed, and returns immediately when no live material can consume it.
            UpdateClusterLights(s);

            // M463: and the light-region record, for the same reason and at the same moment - Mantis reads
            // its SUN out of that buffer rather than out of SUN_LIGHT_COLOR (light-system.md §1.7).
            UpdateLightRegion(s);

            float radius = MathF.Max(0.05f, Mesh?.Radius ?? 1f);
            _frameRadius = radius;   // M646: ShaderCamera scales the headless orbit by the same radius
            var view = s.SuppliedView ?? Matrix4x4.CreateLookAt(
                CameraPosition(s) * radius, Vector3.Zero, Vector3.UnitY);
            if (s.MirrorX) view = Matrix4x4.CreateScale(-1f, 1f, 1f) * view;
            var proj = s.SuppliedProjection ?? Matrix4x4.CreatePerspectiveFieldOfView(
                s.Fov, (float)width / height, radius * 0.02f, radius * 40f);
            var world = s.World;

            // M465: the sun shadow map, FIRST - before the scene's targets are bound and before anything
            // samples it. It binds a depth-stencil view of its own and its own viewport, which the line
            // below then replaces; everything else it touches (raster, blend, depth state, topology, input
            // layout, shaders, constant buffers) the scene pass sets for itself per material.
            //
            // Its side effect is _shadowFrame, which decides for the whole rest of the frame whether
            // FillConstantBuffer uploads a real mShadowProj and real biases, whether StandIn binds the depth
            // texture or the 1x1 white one, and whether the comparison sampler means LessEqual or Always.
            // Those four have to agree, which is why they all read that one field.
            RenderSunShadowMap(s, view, proj);

            var vpRect = new Viewport(0, 0, width, height, 0, 1);
            _ctx.RSSetViewports(1, in vpRect);

            // M460: TWO render targets for the scene pass, whenever the chain can actually run. Riot's
            // environment shaders declare o0 AND o1, and o1 is the glow - see ShaderPreviewRenderer.Bloom.
            // Decided here, once, and remembered for the frame, because BindSceneTargets and the clear and
            // the unbind after the loop all have to agree with each other.
            _glowBound = BloomAvailable(s);
            BindSceneTargets();

            var clear = stackalloc float[4] { s.ClearColor.X, s.ClearColor.Y, s.ClearColor.Z, s.ClearColor.W };
            _ctx.ClearRenderTargetView(_rtv, clear);
            // Black, every frame, and NOT the scene's clear colour: RT1 is a light contribution, so its
            // zero is black. Clearing it to the viewport background would add the background to every
            // pixel the geometry misses and turn the whole frame into a uniform haze.
            if (_glowBound)
            {
                var glowClear = stackalloc float[4] { 0f, 0f, 0f, 1f };
                _ctx.ClearRenderTargetView(_glowRtv, glowClear);
            }
            _ctx.ClearDepthStencilView(_dsv, (uint)ClearFlag.Depth, 1f, 0);

            // M362: the sky, FIRST and before any geometry - exactly where the GL viewport draws it. It
            // writes no depth and tests none, so the scene simply paints over it; drawing it here rather
            // than last also means it never has to be sorted against transparent geometry. No-op until a
            // host sets a sky source. It sets its own raster/blend/depth states, which the three lines
            // below then reset for the scene pass.
            DrawSky(view, proj);

            _ctx.RSSetState(_raster);
            var factor = stackalloc float[4] { 0, 0, 0, 0 };
            _ctx.OMSetBlendState(_blend, factor, 0xFFFFFFFF);
            _ctx.OMSetDepthStencilState(_depthState, 0);

            _ctx.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
            // M264: bound per draw now, because one frame can contain both sources. -1 forces the first
            // draw to bind rather than inheriting whatever the previous frame left set.
            int boundSource = -1;

            bool compare = s.UseComparisonShader && _comparePs.Handle is not null;
            // M661: the debug views replace Riot's pixel shader with a generated one. Not compatible with
            // the comparison shader, which is the same trick for a different question, so it wins.
            bool debugPass = !compare && s.DebugMode >= FirstDebugMode;
            DebugDraws = 0;
            DebugFallbacks = 0;

            // M245: six planes from the combined view-projection, Gribb-Hartmann. Extracted once per
            // frame, not per slice.
            var planes = ExtractFrustum(Matrix4x4.Multiply(view, proj));
            CulledSlices = 0;
            GeometryDraws = 0;
            HiddenSlices = 0;

            // M214: one pass per material. A champion skin is one vertex/index buffer whose submeshes each
            // want their own shader, permutation and textures, so the pipeline is rebound per slice.
            // M246: draw order.
            //
            // Sorting by pipeline collapses state changes, but it CANNOT be applied blindly: reordering
            // alpha-blended draws changes the image, and for particles the authored `pass` order is the
            // artist's intent. So only draws that WRITE DEPTH are sorted - those resolve by the depth
            // buffer rather than by submission order, so their relative order is not observable. Everything
            // else keeps the order it was added in, and is drawn after, still in that order.
            _drawOrder.Clear();
            for (int i = 0; i < _materials.Count; i++) _drawOrder.Add(i);
            if (s.SortByPipeline)
            {
                _drawOrder.Sort((a, b) =>
                {
                    var ma = _materials[a]; var mb = _materials[b];
                    bool sa = ma.SortableByPipeline, sb2 = mb.SortableByPipeline;
                    if (sa != sb2) return sa ? -1 : 1;          // depth-writing first
                    if (!sa) return a.CompareTo(b);             // order-sensitive: keep submission order
                    int p = ma.PipelineId.CompareTo(mb.PipelineId);
                    return p != 0 ? p : a.CompareTo(b);         // stable within a pipeline
                });
            }

            int lastPipeline = int.MinValue;
            PipelineSwitches = 0;
            bool sceneCaptured = false;
            bool depthCaptured = false;
            RibbonDraws = 0;
            DistortionDraws = 0;
            MeshDraws = 0;

            foreach (var drawIndex in _drawOrder)
            {
            var mat = _materials[drawIndex];
            if (!mat.Visible) { HiddenSlices++; continue; }

            // M245: frustum cull. Slices with no bounds are always drawn.
            if (mat.Bounds is { } bb && !FrustumContains(planes, bb.Min, bb.Max)) { CulledSlices++; continue; }

            // M354: per-material back-face culling, matching GL's M34 rule. The global toggle still wins:
            // turning CullBackFaces off forces everything two-sided, which is what that toggle is for.
            //
            // M540: that is what the sentence above always SAID, and the operator was OR, which does the
            // opposite - a global "off" could never force anything two-sided because the material's own
            // flag still turned culling on. GL's rule is `cullBackfaces && !s.DoubleSided`, an AND, and
            // this now matches it. Decals are single-sided by material, so under OR they were culled no
            // matter what the toggle said and were simply absent from the D3D11 image.
            //
            // Set unconditionally rather than tracked. The distortion and mesh-particle branches inside
            // this same loop bind rasterizer state of their own, so any "what did I last bind" flag here
            // would go stale behind them and silently cull the wrong draws. RSSetState is a pointer swap.
            _ctx.RSSetState(s.CullBackFaces && mat.CullBackFaces ? _rasterCull : _raster);

            // M363: snapshot the depth on the FIRST soft-particle material, lazily and once per frame, for
            // the same reason the colour copy is lazy - most frames contain no soft particle at all, and a
            // full-target copy is not free. Here in the draw loop rather than before it, so the snapshot
            // holds the opaque geometry that has already drawn and none of the particles that have not.
            //
            // M720: or on the first particle that WRITES depth, whichever comes first. A NONE emitter writes
            // depth, and one drawn ahead of the first soft particle would otherwise land in the snapshot and
            // fade soft particles against a particle mesh. GL copies depth before any particle draws.
            if ((mat.NeedsSceneDepth || mat.ParticleBlend is { WritesDepth: true }) && !depthCaptured)
            { CaptureDepthCopy(); depthCaptured = true; }

            // M282: heat haze takes a pipeline of its own. Handled before any of the ordinary material
            // state below, because none of it applies - different shaders, different layout, different
            // cbuffer, and a blend mode that overrides what the emitter authored.
            if (mat.DistortionStrength is { } strength)
            {
                if (!sceneCaptured) { CaptureSceneCopy(); sceneCaptured = true; }
                if (DrawDistortion(mat, strength, Matrix4x4.Multiply(view, proj), width, height, ref boundSource))
                { DistortionDraws++; DrawCalls++; }
                continue;
            }

            // M364: beam/trail ribbons, for the same reason as the two branches around it - the strip is
            // world-space geometry in its own buffer with its own shader, so none of the billboard state
            // below applies. Drawn here rather than in a separate pass so it keeps its authored position in
            // the emitter's pass order, which is what decides how it layers against the other emitters.
            if (mat.RibbonId is not null)
            {
                if (DrawRibbon(mat, Matrix4x4.Multiply(view, proj))) DrawCalls++;
                continue;
            }

            // M283: mesh-primitive emitters, likewise handled before the ordinary material state - they
            // draw their own geometry, so even the vertex buffer below does not apply to them.
            if (mat.MeshGeometryId is not null)
            {
                int before = MeshDraws;
                if (DrawMeshParticles(mat, Matrix4x4.Multiply(view, proj)))
                {
                    DrawCalls += MeshDraws - before;
                    // The mesh path binds its own buffers, so whatever the loop thought was bound is stale.
                    boundSource = -1;
                }
                continue;
            }

            // M232: blend is per MATERIAL, not per frame. Particle emitters in one system routinely mix
            // additive and straight-alpha passes, so binding one state before the loop cannot represent
            // them. Non-particle materials leave Additive false and get exactly the previous behaviour.
            // M720: a particle material carries the engine's own state for its mode; the two older answers
            // below remain for everything that is not a particle.
            var particleState = mat.ParticleBlend is { } particleBlend ? ParticleBlendState(particleBlend) : default;
            if (particleState.Handle is not null)
                _ctx.OMSetBlendState(particleState, factor, 0xFFFFFFFF);
            else if (mat.Additive && _blendAdditive.Handle is not null)
                _ctx.OMSetBlendState(_blendAdditive, factor, 0xFFFFFFFF);
            else if (s.AlphaBlend && mat.UsesAuthoredColorBlend)
            {
                var authoredBlend = AuthoredBlendState(mat.SourceColorBlend, mat.DestinationColorBlend);
                _ctx.OMSetBlendState(authoredBlend.Handle is not null ? authoredBlend : _blend, factor, 0xFFFFFFFF);
            }
            else
                _ctx.OMSetBlendState(_blend, factor, 0xFFFFFFFF);

            // M266: and so is the depth WRITE, for the same reason. A particle quad tests against the map
            // but must not deposit depth, or the next additive quad behind it is rejected and the map is
            // occluded by something the artist authored as transparent.
            _ctx.OMSetDepthStencilState(DepthStateFor(mat), 0);

            if (mat.PipelineId != lastPipeline) { PipelineSwitches++; lastPipeline = mat.PipelineId; }
            _ctx.IASetInputLayout(mat.Layout);
            _ctx.VSSetShader(mat.Vs, null, 0);
            _ctx.PSSetShader(compare ? _comparePs : mat.Ps, null, 0);

            // Constant buffers, filled from reflection.
            //
            // M216: a buffer whose contents do not depend on the material - PerFrameVertexCB and
            // PerFramePixelCB, which are per FRAME by name and by content - is filled and uploaded once and
            // then shared by every material. On Howling Abyss that is 1,600 draw calls x 2 buffers of
            // Map/fill/Unmap collapsing to two, which is where most of the frame time was going.
            foreach (var cb in mat.VsRefl.ConstantBuffers)
            {
                if (cb.BindPoint < 0) continue;
                var buf = ResolveCb(mat, cb, mat.VsCbs, s, world, view, proj, unboundConstants);
                if (buf.Handle is null) continue;
                _ctx.VSSetConstantBuffers((uint)cb.BindPoint, 1, ref buf);
            }
            foreach (var cb in mat.PsRefl.ConstantBuffers)
            {
                if (cb.BindPoint < 0) continue;
                var buf = ResolveCb(mat, cb, mat.PsCbs, s, world, view, proj, unboundConstants);
                if (buf.Handle is null) continue;
                _ctx.PSSetConstantBuffers((uint)cb.BindPoint, 1, ref buf);
            }

            // M661: AFTER the constant-buffer loops above, not before. The debug constants live at b0 and
            // the loop binds the material's own buffers at their reflected bind points - which include b0,
            // so binding first meant Riot's PerFrame buffer replaced the debug one and every mode read the
            // same garbage as its mode number. Measured: all thirteen views rendered an identical picture
            // with 0 fallbacks, which looks exactly like "the modes do nothing" and is not.
            //
            // Bound after the normal shader is set, so a material whose signature has no debug shader
            // keeps the one already bound and draws normally, counted as a fallback.
            bool debugBound = debugPass && BindDebugPass(mat, s, world);

            // textures and samplers, at the registers the shader declares
            if (debugBound)
            {
                // BindDebugPass bound the five debug slots and the constants at b0/t0..t4. The VERTEX
                // stage still needs its own resources - the debug shader reads what the material's real
                // vertex shader interpolates, so that half has to run unchanged.
            }
            else if (compare)
            {
                var cbBytes = new byte[32];
                var sd = s.SunDirection;
                var vals = new[] { sd.X, sd.Y, sd.Z, 0f, s.SunColor.X, s.SunColor.Y, s.SunColor.Z, 1f };
                for (int i = 0; i < vals.Length; i++) BitConverter.TryWriteBytes(cbBytes.AsSpan(i * 4, 4), vals[i]);
                EnsureCompareCb();
                Upload(_compareCb, cbBytes, cbBytes.Length);
                _ctx.PSSetConstantBuffers(0, 1, ref _compareCb);

                var firstBound = mat.PsRefl.Textures.FirstOrDefault(t => mat.Textures.ContainsKey(t.Name));
                var srv = firstBound is not null ? mat.Textures[firstBound.Name] : _white;
                _ctx.PSSetShaderResources(0, 1, ref srv);
                var samp = MaterialSampler(mat.SamplerAddress);
                _ctx.PSSetSamplers(0, 1, ref samp);
            }
            else
            {
                BindResources(mat, mat.PsRefl, pixel: true);
            }
            BindResources(mat, mat.VsRefl, pixel: false);

            // M676: a placed prop - Riot's skinned shaders over a geometry of its own, drawn once per
            // placement with the bones posed for that placement. Ahead of the emitter branch below, which
            // reads the same geometry id as a per-particle mesh.
            if (mat.CharacterInstances is { } placements && mat.RiotMeshGeometryId is { } propGeometry)
            {
                DrawCharacterInstances(mat, propGeometry, placements, s, view, proj, unboundConstants, debugBound, world);
                boundSource = -1;
                continue;
            }

            // M640: a Riot-shader mesh emitter takes everything bound above and draws its own geometry once
            // per particle; the shared vertex source is left unbound for whoever comes next.
            if (mat.RiotMeshGeometryId is { } riotGeometry)
            {
                DrawRiotMeshInstances(mat, riotGeometry, s, world, view, proj, unboundConstants);
                boundSource = -1;
                continue;
            }

            uint count = mat.IndexCount < 0
                ? (uint)(mat.UsesDynamicMesh ? _dynIndexCount : _indexCount)
                : (uint)mat.IndexCount;
            if (count > 0 && BindMeshSource(mat.UsesDynamicMesh, ref boundSource))
            {
                _ctx.DrawIndexed(count, (uint)Math.Max(0, mat.StartIndex), 0);
                DrawCalls++;
                GeometryDraws++;
            }
            }

            // M460: RT1 comes off here, and everything below draws to the colour target alone.
            //
            // WHICH PASSES BIND TWO TARGETS, AND WHY. Two: the sky and the material loop - i.e. everything
            // that is a rendering of the MAP. That is where Riot's own shaders run, and they are the only
            // shaders in this renderer that declare o1 at all. One: the fallback dynamic-light overlay and
            // every piece of editor furniture below it.
            //
            // The overlay is deliberate rather than incidental. It is ReyEngine's own approximation for
            // materials that could not be pinned to Riot's in-shader light loop; the real path writes its
            // glow contribution from inside the material's own pixel shader, so having the stand-in ALSO
            // deposit into RT1 would make the approximation glow where the real thing does not. Its colour
            // still lands in RT0 and is still composited over, it just does not create bloom.
            //
            // The editor furniture - highlight, icons, bucket grid, gizmo, brush ring, bake box - is not
            // part of the map at all, and a gizmo that blooms is an editor artefact.
            //
            // The particle branches inside the loop above (heat haze, ribbons, mesh emitters) keep both
            // targets bound and declare only o0, as does the sky. MEASURED on a real device rather than
            // reasoned about (`disasm bloomgpu`, part 1): with two RTVs bound and RT1 pre-cleared to a
            // green sentinel, a pixel shader declaring only SV_Target0 reads back (0,255,0,255) - the
            // sentinel intact - while one declaring both overwrites it. "Undefined" was the other
            // plausible answer, and it would have seeded the glow buffer with noise from every particle
            // draw, which on screen would look like an art problem rather than a binding one.
            if (_glowBound) _ctx.OMSetRenderTargets(1, ref _rtv, _dsv);

            // M452/M456: the FALLBACK light pass. Slices whose permutation carries Riot's own light loop
            // were already lit inside their own pixel shader and are skipped here; only the ones that
            // could not be pinned to a USE_DYNAMIC_LIGHTING permutation get the additive overlay.
            int lightDraws = DrawDynamicLights(s, view, proj, planes);
            LogLightPath();

            // M460: the chain and the screen composite, over the finished scene and UNDER the editor
            // furniture - the game composites bloom before its UI layer, and the furniture is this app's
            // equivalent of one. Leaves the scene target and the full viewport bound behind it.
            BloomPasses = 0;
            if (_glowBound) DrawBloom();

            // M269: editor furniture last, over the finished shading.
            HighlightDraws = DrawHighlight(view, proj);
            IconDraws = DrawIcons(view, proj);
            IconDraws += DrawLightRanges(view, proj);   // M659
            int gridDraws = DrawBucketGrid(view, proj);   // M293
            gridDraws += DrawNavGridAndFaces(view, proj);   // M569
            int gizmoDraws = DrawGizmo(view, proj);
 DrawBrushRing(view, proj);   // M361: after the gizmo, same overlay pipeline      // M296, last so it is over everything
            DrawBakeBox(view, proj);     // M412: same overlay pipeline
            DrawBoneLines(view, proj);   // M619: the skeleton, same overlay pipeline
            DrawDummyLines(view, proj);  // M628: the target dummy box, same overlay pipeline
            DrawRangeLines(view, proj);  // M639: the cast-range ring
            DrawCalls += HighlightDraws + IconDraws + gridDraws + gizmoDraws + lightDraws;

            _ctx.CopyResource(_stage, _rt);
            MappedSubresource map = default;
            int hr = _ctx.Map(_stage, 0, Map.Read, 0, ref map);
            if (hr < 0) { error = $"Map(staging) failed 0x{hr:X8}"; return null; }

            int rowBytes = width * 4;
            if (_readback.Length != rowBytes * height) _readback = new byte[rowBytes * height];
            var outBytes = _readback;
            fixed (byte* dst = outBytes)
            {
                // M216: the staging pitch usually equals the row, in which case this is one copy
                if (map.RowPitch == (uint)rowBytes)
                    System.Buffer.MemoryCopy(map.PData, dst, outBytes.Length, outBytes.Length);
                else
                    for (int y = 0; y < height; y++)
                        System.Buffer.MemoryCopy((byte*)map.PData + (nuint)y * map.RowPitch,
                            dst + y * rowBytes, rowBytes, rowBytes);
            }
            _ctx.Unmap(_stage, 0);

            LastFrameMs = sw.Elapsed.TotalMilliseconds;
            return outBytes;
        }
        catch (Exception ex)
        {
            error = $"render failed: {ex.Message}";
            Log(error);
            return null;
        }
    }

    private readonly Dictionary<string, ComPtr<ID3D11Buffer>> _sharedCbs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sharedUploadedThisFrame = new(StringComparer.Ordinal);

    /// <summary>Pick the buffer for this cbuffer and make sure it holds the right bytes. Material-independent
    /// buffers are shared and uploaded at most once per frame.</summary>

    /// <summary>
    /// <para>M265: the cache key for a per-frame shared constant buffer. It must name every input that
    /// changes the BYTES, not just the layout.</para>
    ///
    /// <para>M216 shared these buffers by <c>name + size</c> on the reasoning that a "per frame" buffer
    /// holds the same values for every material, so the first material of the frame can fill it and the
    /// rest can bind it. That is true of the camera and the sun. It is NOT true of two constants, because
    /// <see cref="FillConstantBuffer"/> resolves them from the material:</para>
    /// <list type="bullet">
    ///   <item><c>MPROJ</c> is <c>ParticleStyleProjection(mat) ? vp : proj</c> - the full world-to-clip
    ///   transform for particles, projection alone for anything with a bone buffer.</item>
    ///   <item><c>CSOFTPARTICLECONTROL</c> is selected from <c>mat.Additive</c>.</item>
    /// </list>
    ///
    /// <para>Sharing across that difference is silent and total: a map staticmesh claims
    /// <c>PerFrameVertexCB#560</c> and writes projection alone, then a particle quad binds the same buffer
    /// and transforms its world-space vertices with no view matrix at all - landing on the near plane and
    /// drawing nothing. The draw call succeeds, the counters look right, and the frame is byte-identical
    /// to one without the particle. It only became reachable when M264 let a map and particles share a
    /// frame; before that the two never coexisted.</para>
    ///
    /// <para><b>Invariant:</b> if you add a case to FillConstantBuffer's switch that reads <c>mat</c>,
    /// add it here too. <c>mat.Params</c> is exempt - the materialSpecific test above already routes
    /// those materials to their own buffer. The `coexist` harness mode is the regression check.</para>
    /// </summary>
    private string SharedCbKey(DxbcConstantBuffer cb, PreviewMaterial mat)
        => cb.Name + "#" + cb.AllocationSize
           + (ParticleStyleProjection(mat) ? "#vp" : "#proj")
           // M720: on the soft-fade selector itself, which nine blend modes reduce to three of.
           + (mat.ParticleSoftControl is { } softControl
               ? "#sc" + (int)softControl.X + (int)softControl.Y + (int)softControl.Z + (int)softControl.W
               : mat.Additive ? "#add" : "");

    private ComPtr<ID3D11Buffer> ResolveCb(PreviewMaterial mat, DxbcConstantBuffer cb,
        Dictionary<int, ComPtr<ID3D11Buffer>> own, PreviewSettings s,
        Matrix4x4 world, Matrix4x4 view, Matrix4x4 proj, List<string>? unbound)
    {
        // M676: a material posed by a palette of its own, or drawn once per placement, cannot share a
        // buffer another material filled from the frame's World and palette - its bones and mWorld are its
        // own and are refilled per draw. The invariant above holds: the fill reads mat.BonePalette and
        // _instanceWorld only on this path, and this path never shares.
        bool materialSpecific = mat.CharacterInstances is not null || mat.BonePalette is not null;
        if (!materialSpecific && mat.Params.Count > 0)
            foreach (var v in cb.Variables)
                if (mat.Params.ContainsKey(v.Name)) { materialSpecific = true; break; }

        if (materialSpecific)
        {
            if (!own.TryGetValue(cb.BindPoint, out var mine)) return default;
            FillConstantBuffer(mat, cb, s, world, view, proj, unbound);
            Upload(mine, _pendingCb, _pendingLength);
            return mine;
        }

        string key = SharedCbKey(cb, mat);
        if (!_sharedCbs.TryGetValue(key, out var shared))
        {
            var desc = new BufferDesc
            {
                ByteWidth = (uint)Math.Max(16, cb.AllocationSize),
                Usage = Usage.Dynamic,
                BindFlags = (uint)BindFlag.ConstantBuffer,
                CPUAccessFlags = (uint)CpuAccessFlag.Write,
            };
            ComPtr<ID3D11Buffer> nb = default;
            if (_device.CreateBuffer(in desc, null, ref nb) < 0) return default;
            shared = nb;
            _sharedCbs[key] = shared;
        }
        if (_sharedUploadedThisFrame.Add(key))
        {
            FillConstantBuffer(mat, cb, s, world, view, proj, unbound);
            Upload(shared, _pendingCb, _pendingLength);
        }
        return shared;
    }

    private void BindResources(PreviewMaterial mat, DxbcShader? refl, bool pixel)
    {
        if (refl is null) return;
        foreach (var t in refl.Textures)
        {
            // M363: the scene depth is resolved HERE rather than stored in mat.Textures, because materials
            // hold the SRV itself and this one is recreated on every resize - a stored copy would dangle the
            // first time the viewport changed size. Reading the field per draw is always current.
            if (mat.NeedsSceneDepth && _depthCopySrv.Handle is not null
                && t.Name.Contains("DepthTexture", StringComparison.OrdinalIgnoreCase))
            {
                var d = _depthCopySrv;
                if (pixel) _ctx.PSSetShaderResources(t.BindPoint, 1, ref d);
                else _ctx.VSSetShaderResources(t.BindPoint, 1, ref d);
                continue;
            }
            // M456: engine-owned lighting resources. These are NOT material bindings and never appear in
            // mat.Textures, so without this they took the generic white stand-in - which for
            // CLUSTER_MAP_SharedTexture (a Texture3D<uint>) and DYNAMIC_ENV_LIGHT_IDS (a Texture2D<uint4>)
            // is a type mismatch the runtime resolves as undefined rather than as an error.
            if (IsEngineLightingResource(t.Name))
            {
                var engineOwned = ClusterResourceFor(t.Name);
                if (pixel) _ctx.PSSetShaderResources(t.BindPoint, 1, ref engineOwned);
                else _ctx.VSSetShaderResources(t.BindPoint, 1, ref engineOwned);
                continue;
            }
            var srv = mat.Textures.TryGetValue(t.Name, out var bound) ? bound : StandIn(t);
            if (pixel) _ctx.PSSetShaderResources(t.BindPoint, 1, ref srv);
            else _ctx.VSSetShaderResources(t.BindPoint, 1, ref srv);
        }
        // M456: StructuredBuffers reflect as DxbcResourceKind.Structured, which DxbcShader.Textures
        // filters out - so the loop above cannot reach CLUSTER_DATA_BUFFER at all.
        BindStructuredBuffers(refl, pixel);
        foreach (var smp in refl.Samplers)
        {
            // "Clamp_" prefixed shared samplers are always clamped. Ordinary samplers use the material's
            // authored per-axis address mode; Wrap is only the default when the material says nothing.
            // M254: the shader's own RDEF flag decides this, not the sampler's name. D3D_SIF_COMPARISON_SAMPLER
            // is the only place that distinction is recorded, and binding an ordinary state where the shader
            // uses sample_c fails silently as "fully shadowed" rather than as an error.
            // M465: and which of the TWO comparison states depends on whether this frame rendered a shadow
            // map. Safe to apply to all three shadow samplers at once, not just the sun's: the spot and
            // point maps are still the 1x1 R32_FLOAT stand-in holding 1.0, every one of their references is
            // saturated into [0,1] by the shader before the compare (light-system.md §2.5), so LessEqual
            // against 1.0 returns "lit" exactly as Always did.
            // M635: and a slot the material itself marked as a lookup table clamps too, whatever address
            // mode the rest of the material uses - see PreviewMaterial.ClampedSamplers.
            var st = smp.IsComparisonSampler ? (_shadowFrame is null ? _comparison : _comparisonLessEqual)
                : smp.Name.StartsWith("Clamp", StringComparison.OrdinalIgnoreCase) ? _linearClamp
                : mat.ClampedSamplers is { } clamped && clamped.Contains(smp.Name) ? _linearClamp
                // M717: and a slot that authored its own address mode takes it, whatever the material's is.
                : mat.SlotAddress is { } perSlot && perSlot.TryGetValue(smp.Name, out int mode)
                    ? AuthoredSampler(mode)
                : MaterialSampler(mat.SamplerAddress);
            if (pixel) _ctx.PSSetSamplers(smp.BindPoint, 1, ref st);
            else _ctx.VSSetSamplers(smp.BindPoint, 1, ref st);
        }
    }

    /// <summary>M717: Riot's address enum. 3 is folded to the mirror rather than to a border mode: neither
    /// this repo nor the renderer the reading came from has measured what it means, they read it as a
    /// border and we read it as a mirror, and it is 599 emitters either way.</summary>
    private ComPtr<ID3D11SamplerState> AuthoredSampler(int mode) => mode switch
    {
        0 => _linearWrap,
        1 => _linearClamp,
        _ => _linearMirror.Handle is not null ? _linearMirror : _linearWrap,
    };

    private ComPtr<ID3D11SamplerState> MaterialSampler(PreviewSamplerAddress address) => address switch
    {
        PreviewSamplerAddress.ClampU => _linearClampU,
        PreviewSamplerAddress.ClampV => _linearClampV,
        PreviewSamplerAddress.ClampUV => _linearClamp,
        _ => _linearWrap,
    };

    /// <summary>The stand-in for a texture nothing supplied. White for almost everything; an identity
    /// ramp for the colour remap, where white would replace the whole image.</summary>
    private ComPtr<ID3D11ShaderResourceView> StandIn(DxbcResource resource)
    {
        if (resource.Name.Contains("REMAP_RAMP", StringComparison.OrdinalIgnoreCase)) return _identityRamp;

        // M465: the SUN shadow map, when this frame rendered one. StartsWith, not Contains, and this arm
        // must stay above the stand-in below: SPOT_SHADOW_MAP_DEPTH_PCF and POINT_SHADOW_MAP_DEPTH_PCF both
        // CONTAIN this name, and neither is this texture - the point one is a cubemap, and handing either a
        // 2D view of the sun's depth would shadow every spot and point light with the sun's silhouette.
        // Those two keep the stand-in; spot and point shadows are out of scope for this milestone.
        if (_shadowFrame is not null && _shadowSrv.Handle is not null
            && resource.Name.StartsWith("SHADOW_MAP_DEPTH_PCF", StringComparison.OrdinalIgnoreCase))
            return _shadowSrv;

        // M366: shadow maps are read with sample_c, which is undefined against the RGBA8 white stand-in.
        // See the _whiteDepth creation for the measurement.
        if (_whiteDepth.Handle is not null
            && (resource.Name.Contains("SHADOW_MAP", StringComparison.OrdinalIgnoreCase)
                || resource.Name.Contains("DEPTH_PCF", StringComparison.OrdinalIgnoreCase)))
            return _whiteDepth;
        // M463: the map's authored skyLightColor, as the ambient environment the PBR family samples. This
        // arm is reached ONLY when nothing else bound the slot, so a map shipping a real IBL probe keeps it.
        // See UpdateSkyAmbient for why the sky lands here and not on a PerFramePixelCB constant.
        if (_skyIblSrv.Handle is not null
            && resource.Name.Contains("IBL_CUBEMAP", StringComparison.OrdinalIgnoreCase))
            return _skyIblSrv;
        return resource.Dimension switch
        {
            5 => _whiteArray,
            9 => _whiteCube,
            10 => _whiteCubeArray,
            _ => _white,
        };
    }


    /// <summary>Which reflected textures currently have nothing bound (they sample a stand-in).</summary>
    public IEnumerable<string> UnboundTextureNames() =>
        _materials.SelectMany(m => m.UnboundTextures).Distinct(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- teardown

    public void Dispose()
    {
        ClearMaterials();
        ClearTextures();
        _rasterCull.Dispose();
        _brushRingVb.Dispose();
        _white.Dispose();
        _whiteArray.Dispose();
        _whiteCube.Dispose();
        _whiteDepth.Dispose();
        _whiteCubeArray.Dispose();
        _identityRamp.Dispose();
        // M242: the cache owns shader objects that no material releases, so it must be drained here or
        // every pipeline ever built leaks for the lifetime of the process.
        ClearPipelineCache();
        _vb.Dispose(); _ib.Dispose(); _dynVb.Dispose(); _dynIb.Dispose(); _compareCb.Dispose();
        _overlayVs.Dispose(); _overlayPs.Dispose(); _overlayLayout.Dispose();
        _overlayCb.Dispose(); _overlayDepth.Dispose(); _overlayDepthNoTest.Dispose(); _overlayBlend.Dispose();
        _distortVs.Dispose(); _distortPs.Dispose(); _distortLayout.Dispose(); _distortCb.Dispose();
        _sceneCopySrv.Dispose(); _sceneCopy.Dispose();
        _depthCopySrv.Dispose(); _depthCopy.Dispose();
        _gridVs.Dispose(); _gridPs.Dispose(); _gridLayout.Dispose(); _gridVb.Dispose();
        _gizmoVb.Dispose();
        _gizmoRaster.Dispose();
        _lightRangeVb.Dispose();
        DisposeDebugViews();   // M661
        _bakeBoxVb.Dispose();
        _boneVb.Dispose();   // M619
        _dummyVb.Dispose();  // M628
        _rangeVb.Dispose();  // M639
        DisposeSky();
        DisposeRibbon();
        DisposeDynamicLights();
        DisposeClusterLights();
        DisposeBloom();
        DisposeShadow();
        _meshVs.Dispose(); _meshPs.Dispose(); _meshLayout.Dispose(); _meshCb.Dispose();
        _meshCullCw.Dispose(); _meshCullCcw.Dispose();
        ReleaseMeshGeometry();
        _iconVb.Dispose(); _iconIb.Dispose();
        _overlayVsTex.Dispose(); _overlayPsTex.Dispose(); _overlayLayoutTex.Dispose(); _iconSampler.Dispose();
        for (int g = 0; g < _glyphSrv.Length; g++) _glyphSrv[g].Dispose();
        _rtv.Dispose(); _rt.Dispose(); _stage.Dispose(); _dsv.Dispose(); _depth.Dispose();
        _linearWrap.Dispose(); _linearClampU.Dispose(); _linearClampV.Dispose(); _linearClamp.Dispose();
        _comparison.Dispose();
        _comparisonLessEqual.Dispose();
        foreach (var state in _authoredBlendStates.Values) state.Dispose();
        _authoredBlendStates.Clear();
        foreach (var state in _particleBlendStates.Values) state.Dispose();   // M720
        _particleBlendStates.Clear();
        _raster.Dispose(); _blend.Dispose(); _blendOpaque.Dispose(); _depthState.Dispose();
        _blendAdditive.Dispose(); _depthStateNoWrite.Dispose(); _depthStateNoTest.Dispose();
        _linearMirror.Dispose();
        _ctx.Dispose(); _device.Dispose();
        _d3d?.Dispose();
    }
}
