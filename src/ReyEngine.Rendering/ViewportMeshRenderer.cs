using System.Numerics;
using Silk.NET.OpenGL;

namespace ReyEngine.Rendering;

/// <summary>
/// Renders a preview mesh: per-submesh textured diffuse (solid), wireframe (line-indexed),
/// plus optional bounding box and skeleton bone overlays.
/// </summary>
public sealed class ViewportMeshRenderer : IDisposable
{
    private GL _gl = null!;
    private bool _ready;

    private uint _meshProgram, _lineProgram;
    private int _mMvp, _mModel, _mLight, _mSunColor, _mSkyLight, _mTex, _mHasTex, _mBaseColor, _mMode, _mCamPos;
    private int _mMask, _mGradient, _mEmissive, _mHasMask, _mHasGradient, _mHasEmissive;
    private int _mMatCap, _mMatCapMask, _mHasMatCap, _mHasMatCapMask, _mView;
    private int _mUvScaleOffset, _mUvRot, _mUsesRim, _mUsesSpec;   // M32: per-material UV + feature flags
    private int _mHasVertexColor;                                  // M33: mapgeo PrimaryColor present
    private int _mLightmap, _mHasLightmap;                         // M33: baked lightmap atlas (slot 6, Texcoord7 UV)
    private int _mAlphaMode;                                       // M34: 0 opaque, 1 cutout, 2 transparent, 3 transparent cutout
    private int _mTint;                                            // M34: TintColor for untextured effects / authored decals
    private int _mTintTextured;
    private int _mAlphaCutoff;                                     // M34: per-material alpha-test threshold (AlphaTestValue)
    private int _mClampUv;                                         // M34: per-axis UV clamp (decals; addressU/V == Clamp)
    private int _mTwoSided, _mMirrored;                            // M34: two-sided lighting + mirrored-transform debug
    private int _mIsFlowmap, _mTime, _mFlowSpeed, _mFlowStrength;  // M44: flowmap river water
    private int _mFlowTile, _mColorInside, _mColorOutside, _mWaterAlpha;
    private int _mIsTerrainBlend, _mTerrainWorldScale;             // terrain shader 0xe25b830f
    private int _mTerrainBottomTiling, _mTerrainMiddleTiling, _mTerrainTopTiling, _mTerrainExtrasTiling;
    private int _mTerrainMaskMultipliers;
    private int _mLightmapScale;                                  // M45: MapSunProperties.lightMapColorScale
    private int _lMvp, _lColor;
    private float _time;                                          // M44: animation clock (seconds), fed to uTime
    private float _lightmapScale = 1f;                            // M45: baked-light multiplier (1 = neutral)
    private Vector3 _lightDirection = new(-0.4f, -0.85f, -0.45f);
    private Vector3 _sunColor = new(0.75f);
    private Vector3 _skyLight = new(0.35f);

    private uint _vao, _vbo, _ebo, _wireEbo, _colorVbo, _lightmapUvVbo;
    private bool _hasVertexColor, _hasLightmapUv;
    private int _indexCount, _wireIndexCount;
    private bool _hasMesh;
    private float[]? _interleaved; // kept so per-frame skinning can update pos+normal in place
    private int _meshVertexCount;

    private uint _whiteTex;
    private SubmeshDraw[] _submeshes = Array.Empty<SubmeshDraw>();
    private readonly List<uint> _ownedTextures = new(); // unique textures, shared across submeshes

    private uint _boundsVao, _boundsVbo;
    private uint _boneVao, _boneVbo;
    private uint _highlightVao, _highlightVbo;
    private uint _groupBoundsVao, _groupBoundsVbo;
    private uint _gizmoVao, _gizmoVbo;
    private uint _particleVao, _particleVbo, _particleSelVao, _particleSelVbo;   // M35: placed-particle markers
    private uint _propVao, _propVbo, _probeVao, _probeVbo;                       // M38: prop / cubemap-probe markers
    private int _boundsVerts, _boneVerts, _highlightVerts, _groupBoundsVerts;
    private int _particleVerts, _particleSelVerts, _propVerts, _probeVerts;

    // M41: placed animated-prop meshes — unique geometry registered once, instanced per placement.
    private sealed class PropGeometry
    {
        public uint Vao, Vbo, Ebo;
        public (int Start, int Count, uint Tex)[] Submeshes = System.Array.Empty<(int, int, uint)>();
        public float[]? Interleaved;   // M54: cached stride-8 stream so idle animation can re-skin pos/normals
    }
    private readonly List<PropGeometry> _propGeoms = new();
    private readonly List<(int Geo, Matrix4x4 Model)> _propMeshInstances = new();
    private readonly List<uint> _propTextures = new();
    private bool _hasGizmo;
    private Vector3 _gizmoPivot;
    private float _gizmoArmLength;
    private int _gizmoMode;                                   // M42: 0 move · 1 rotate · 2 scale
    private Vector3 _gizmoAxX = Vector3.UnitX, _gizmoAxY = Vector3.UnitY, _gizmoAxZ = Vector3.UnitZ;

    private struct SubmeshDraw
    {
        public int Start;
        public int Count;
        public uint Texture;     // slot 0 diffuse
        public uint Mask;        // slot 1
        public uint Gradient;    // slot 2
        public uint Emissive;    // slot 3
        public uint MatCap;      // slot 4
        public uint MatCapMask;  // slot 5
        public uint Lightmap;    // slot 6 (map baked lightmap atlas)
        public bool Visible;     // layer/visibility filter (map dragon/baron)

        // M32 per-material preview data. Defaults are identity/off so untouched submeshes render as before.
        public Vector4 UvScaleOffset;   // xy scale, zw offset
        public float UvRotationRadians;
        public bool UsesRim;
        public bool UsesSpecular;
        public int AlphaMode;           // M34: 0 opaque, 1 cutout, 2 transparent, 3 transparent cutout
        public float AlphaCutoff;       // M34: alpha-test threshold for cutout (default 0.35)
        public bool DoubleSided;        // M34: two-sided material (cullEnable=false) — never culled
        public bool Mirrored;           // M34: source mesh has a negative-determinant (mirrored) transform
        public Vector2 ClampUv;         // M34: per-axis UV clamp (1 = clamp/decal, 0 = tile); default 0,0
        public Vector4 Tint;            // M34: TintColor; default 1,1,1,1
        public bool TintTextured;        // multiply a diffuse texture too (authored soft decals)

        // M44 flowmap river water (Bloom_FlowMapRiver_*). Flow_Map -> Mask (slot 1), Flowing_Normal -> Gradient (slot 2).
        public bool IsFlowmap;
        public float FlowSpeed;
        public float FlowStrength;
        public Vector2 FlowTile;
        public Vector4 ColorInside;
        public Vector4 ColorOutside;
        public float WaterAlpha;

        // Terrain shader 0xe25b830f. Slots are reused as mask/middle/top/extras for this special branch.
        public bool IsTerrainBlend;
        public Vector2 TerrainBottomTiling;
        public Vector2 TerrainMiddleTiling;
        public Vector2 TerrainTopTiling;
        public Vector2 TerrainExtrasTiling;
        public float TerrainWorldScale;
        public Vector3 TerrainMaskMultipliers;

        public static SubmeshDraw Create(int start, int count) =>
            new() { Start = start, Count = count, Visible = true, UvScaleOffset = new Vector4(1, 1, 0, 0), Tint = Vector4.One, AlphaCutoff = 0.35f };
    }

    /// <summary>Per-submesh preview material data pushed from the App's resolved <c>MaterialProfile</c> (M32/M34).
    /// <paramref name="Mirrored"/> is a per-mesh geometric flag (negative-determinant transform), not a material one.</summary>
    public readonly record struct SubmeshMaterial(
        bool UsesRim, bool UsesSpecular, Vector2 UvScale, Vector2 UvOffset, float UvRotationDegrees,
        int AlphaMode = 0, bool DoubleSided = false, Vector4? Tint = null, bool TintTextured = false,
        bool Mirrored = false, float AlphaCutoff = 0.35f,
        bool ClampU = false, bool ClampV = false,
        // M44 flowmap river water: a Flowmap_River material animates flowing water; Flow_Map on tex slot 1,
        // Flowing_Normal_Map on slot 2, diffuse on slot 0. FlowTile/Speed/Strength drive the animation.
        bool IsFlowmap = false, float FlowSpeed = 0f, float FlowStrength = 0f, Vector2 FlowTile = default,
        Vector4 ColorInside = default, Vector4 ColorOutside = default, float WaterAlpha = 1f,
        // Terrain blend: slot 0 bottom, slot 1 mask, slot 2 middle, slot 3 top, slot 4 extras.
        bool IsTerrainBlend = false, Vector2 TerrainBottomTiling = default, Vector2 TerrainMiddleTiling = default,
        Vector2 TerrainTopTiling = default, Vector2 TerrainExtrasTiling = default, float TerrainWorldScale = 1f,
        Vector3 TerrainMaskMultipliers = default)
    {
        public static readonly SubmeshMaterial Default = new(false, false, Vector2.One, Vector2.Zero, 0f);
    }

    private const string MeshVert = @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUv;
layout(location = 3) in vec4 aColor;   // PrimaryColor (defaults to white via VertexAttrib4f when absent)
layout(location = 4) in vec2 aLmUv;    // baked-lightmap atlas UV (already uv7*scale+bias; 0 when absent)
uniform mat4 uMvp;
uniform mat4 uModel;
out vec3 vN;
out vec2 vUv;
out vec3 vWorld;
out vec4 vColor;
out vec2 vLmUv;
void main() {
    vN = mat3(uModel) * aNormal;
    vUv = aUv;
    vColor = aColor;
    vLmUv = aLmUv;
    vWorld = (uModel * vec4(aPos, 1.0)).xyz;
    gl_Position = uMvp * vec4(aPos, 1.0);
}";

    // uMode: 0 Basic · 1 RiotApprox · 2 Debug base · 3 Debug alpha · 4 Debug normal · 5 Debug mask
    //        6 Debug emissive · 7 Debug matcap · 8 Debug UV checker · 9 Debug specular · 10 Debug vertex color
    //        11 Debug lightmap · 12 Face orientation · 13 Two-sided highlight · 14 Mirrored highlight
    // M32: rim + specular are gated per-material (uUsesRim/uUsesSpec) — no fake specular by default.
    // The base UV is transformed per-material by uUvScaleOffset (xy scale, zw offset) + uUvRot (radians).
    // M33: when a mesh carries a baked lightmap (uHasLightmap), the atlas replaces the fake directional
    //      term (col = base * lightmap) — real baked light instead of an invented one.
    private const string MeshFrag = @"
in vec3 vN;
in vec2 vUv;
in vec3 vWorld;
in vec4 vColor;
in vec2 vLmUv;
out vec4 FragColor;
uniform vec3 uLight;
uniform vec3 uSunColor;
uniform vec3 uSkyLight;
uniform sampler2D uTex;
uniform sampler2D uMask;
uniform sampler2D uGradient;
uniform sampler2D uEmissive;
uniform sampler2D uMatCap;
uniform sampler2D uMatCapMask;
uniform int uHasTex;
uniform int uHasMask;
uniform int uHasGradient;
uniform int uHasEmissive;
uniform int uHasMatCap;
uniform int uHasMatCapMask;
uniform vec3 uBaseColor;
uniform int uMode;
uniform vec3 uCamPos;
uniform mat4 uView;
uniform vec4 uUvScaleOffset;   // xy = scale, zw = offset (identity = 1,1,0,0)
uniform float uUvRot;          // radians, rotate about (0.5, 0.5); 0 = none
uniform int uUsesRim;          // fresnel rim highlight only when the material profile asks for it
uniform int uUsesSpec;         // specular highlight only when the material profile asks for it
uniform int uHasVertexColor;   // 1 when the mesh carries PrimaryColor (map baked-term/mask data)
uniform sampler2D uLightmap;   // baked lightmap atlas (slot 6)
uniform int uHasLightmap;      // 1 when the mesh has a BakedLight atlas + Texcoord7 UV
uniform int uAlphaMode;        // M34: 0 opaque, 1 cutout, 2 transparent, 3 transparent cutout
uniform float uAlphaCutoff;    // M34: alpha-test threshold for cutout mode (from AlphaTestValue; default 0.35)
uniform vec2 uClampUv;         // M34: per-axis UV clamp (1 = clamp to [0,1] for decals; 0 = tile)
uniform vec4 uTint;            // M34: TintColor (rgba); 1,1,1,1 = none
uniform int uTintTextured;     // 1 when TintColor also multiplies the diffuse texture (soft decals)
uniform int uTwoSided;         // M34: 1 when this face renders two-sided (flip backface normals for lighting)
uniform int uMirrored;         // M34: 1 when the source mesh transform is mirrored (negative determinant)
// M44 flowmap river water: Flow_Map (rg = flow dir) reuses slot 1, Flowing_Normal_Map reuses slot 2.
uniform float uLightmapScale;  // M45: MapSunProperties.lightMapColorScale (baked-light multiplier; 1 when absent)
uniform int uIsFlowmap;        // 1 when this submesh is a Flowmap_River water material
uniform float uTime;           // seconds, drives the flow animation
uniform float uFlowSpeed;      // FlowMap_Speed
uniform float uFlowStrength;   // Flowmap_Strength
uniform vec2 uFlowTile;        // FlowNormal_Tile
uniform vec4 uColorInside;     // Color_Inside (deep water)
uniform vec4 uColorOutside;    // Color_Outside (edge water)
uniform float uWaterAlpha;     // TranslucentControl
// Terrain shader 0xe25b830f: RGB mask blends middle/top/extras over the bottom layer.
uniform int uIsTerrainBlend;
uniform float uTerrainWorldScale;
uniform vec2 uTerrainBottomTiling;
uniform vec2 uTerrainMiddleTiling;
uniform vec2 uTerrainTopTiling;
uniform vec2 uTerrainExtrasTiling;
uniform vec3 uTerrainMaskMultipliers;

// League MatCap_Tex: a spheremap of fake studio lighting sampled by the view-space normal.
vec3 matcapColour(vec3 worldN) {
    vec3 vn = normalize(mat3(uView) * worldN);
    vec2 uv = vn.xy * 0.5 + 0.5;
    return texture(uMatCap, uv).rgb;
}
// Per-material UV transform: optional rotation about the centre, then scale + offset.
vec2 xformUv(vec2 uv0) {
    vec2 uv = uv0;
    if (uUvRot != 0.0) {
        float c = cos(uUvRot), s = sin(uUvRot);
        vec2 p = uv - 0.5;
        uv = vec2(p.x * c - p.y * s, p.x * s + p.y * c) + 0.5;
    }
    return uv * uUvScaleOffset.xy + uUvScaleOffset.zw;
}
// Map diffuse textures are already display encoded, but BakedLight stores linear illumination. Encode
// only that illumination before it modulates the diffuse. Encoding the final colour also re-encodes the
// diffuse and noticeably over-brightens the map.
vec3 bakedLightColour(vec3 light) {
    return pow(max(light, vec3(0.0)), vec3(0.45454545));
}
void main() {
    vec3 n = length(vN) > 0.001 ? normalize(vN) : vec3(0.0, 1.0, 0.0);
    // M34 two-sided lighting: a back-facing fragment of a two-sided (cullEnable=false) material would shade
    // with an away-pointing normal (dark/black). gl_FrontFacing tells us which side we're on; flip so the
    // normal faces the viewer. Single-sided materials keep their normal (their backfaces are culled anyway).
    if (uTwoSided == 1 && !gl_FrontFacing) n = -n;
    vec2 uv = xformUv(vUv);
    // M34 texture address: decals use Clamp so their out-of-[0,1] UVs show the decal ONCE (edge texel,
    // usually transparent -> cut out) instead of GL_REPEAT tiling it across the whole mesh.
    if (uClampUv.x > 0.5) uv.x = clamp(uv.x, 0.0, 1.0);
    if (uClampUv.y > 0.5) uv.y = clamp(uv.y, 0.0, 1.0);
    // Untextured effect/indicator materials use TintColor as their colour. Soft decals additionally multiply
    // their diffuse by the authored tint; ordinary textured materials remain unchanged.
    vec4 tex = (uHasTex == 1) ? texture(uTex, uv) : vec4(uBaseColor * uTint.rgb, uTint.a);
    if (uHasTex == 1 && uTintTextured == 1) tex *= uTint;
    vec3 base = tex.rgb;
    float alpha = tex.a;

    // The mask uses the mesh UV atlas. Detail layers are planar world-space textures and are stacked in
    // authored order: R selects Middle, G selects Top, B selects Extras. The pass blend flag describes this
    // internal texture blend; the final terrain surface remains opaque.
    if (uIsTerrainBlend == 1) {
        vec2 worldUv = vWorld.xz * uTerrainWorldScale;
        vec3 bottom = (uHasTex == 1) ? texture(uTex, worldUv * uTerrainBottomTiling).rgb : uBaseColor;
        vec3 middle = (uHasGradient == 1) ? texture(uGradient, worldUv * uTerrainMiddleTiling).rgb : bottom;
        vec3 top = (uHasEmissive == 1) ? texture(uEmissive, worldUv * uTerrainTopTiling).rgb : middle;
        vec3 extras = (uHasMatCap == 1) ? texture(uMatCap, worldUv * uTerrainExtrasTiling).rgb : top;
        vec3 weights = (uHasMask == 1) ? texture(uMask, uv).rgb * uTerrainMaskMultipliers : vec3(0.0);
        base = mix(bottom, middle, clamp(weights.r, 0.0, 1.0));
        base = mix(base, top, clamp(weights.g, 0.0, 1.0));
        base = mix(base, extras, clamp(weights.b, 0.0, 1.0));
        alpha = 1.0;
    }

    if (uMode == 2) { FragColor = vec4(base, 1.0); return; }                 // debug: base/diffuse
    if (uMode == 3) { FragColor = vec4(vec3(alpha), 1.0); return; }          // debug: alpha
    if (uMode == 4) { FragColor = vec4(n * 0.5 + 0.5, 1.0); return; }        // debug: normals
    if (uMode == 5) {                                                        // debug: mask (white if none)
        vec3 mk = (uHasMask == 1) ? texture(uMask, uv).rgb : vec3(1.0);
        FragColor = vec4(mk, 1.0); return;
    }
    if (uMode == 6) {                                                        // debug: emissive (black if none)
        vec3 em = (uHasEmissive == 1) ? texture(uEmissive, uv).rgb : vec3(0.0);
        FragColor = vec4(em, 1.0); return;
    }
    if (uMode == 7) {                                                        // debug: matcap (grey if none)
        vec3 mc = (uHasMatCap == 1) ? matcapColour(n) : vec3(0.2);
        FragColor = vec4(mc, 1.0); return;
    }
    if (uMode == 8) {                                                        // debug: UV checker (post-transform)
        vec2 t = floor(uv * 8.0);
        float c = mod(t.x + t.y, 2.0);
        FragColor = vec4(mix(vec3(0.14, 0.15, 0.19), vec3(0.85, 0.86, 0.92), c), 1.0); return;
    }
    if (uMode == 10) {                                                       // debug: vertex color (magenta if none)
        FragColor = uHasVertexColor == 1 ? vec4(vColor.rgb, 1.0) : vec4(0.8, 0.0, 0.8, 1.0); return;
    }
    if (uMode == 11) {                                                       // debug: lightmap (dark blue if none)
        FragColor = uHasLightmap == 1 ? vec4(texture(uLightmap, vLmUv).rgb, 1.0) : vec4(0.03, 0.03, 0.08, 1.0); return;
    }
    if (uMode == 12) {                                                       // debug: face orientation (front green / back red)
        FragColor = gl_FrontFacing ? vec4(0.15, 0.80, 0.30, 1.0) : vec4(0.90, 0.20, 0.20, 1.0); return;
    }
    if (uMode == 13) {                                                       // debug: two-sided (cullEnable=false) materials
        FragColor = uTwoSided == 1 ? vec4(0.25, 0.65, 1.0, 1.0) : vec4(0.13, 0.14, 0.17, 1.0); return;
    }
    if (uMode == 14) {                                                       // debug: mirrored (negative-determinant) meshes
        FragColor = uMirrored == 1 ? vec4(1.0, 0.55, 0.12, 1.0) : vec4(0.13, 0.14, 0.17, 1.0); return;
    }

    vec3 viewDir = normalize(uCamPos - vWorld);

    // M44 Flowmap_River water (real material: USE_DIFFUSE_TEXTURE=off, colour is pure Color_Inside/Outside,
    // NOT a diffuse map). The Flow_Map on slot 1 packs its channels: R = flow, G = opacity (where water IS),
    // B = specular mask. A tiled water-wave normal (Flowing_Normal_Map, slot 2) is flow-advected in two
    // cross-faded phases and drives a sharp SPECULAR glint; a fresnel term tints deep(Inside) -> edge(Outside).
    if (uIsFlowmap == 1) {
        // Flow_Map channels (author-confirmed by splitting the real texture):
        //   B = alpha mask: white stream lines = where the water IS; black = nothing drawn.
        //   R = gradient along each stream: animation phase (light travels down the line) + reflection gate.
        //   G = signed flow component centred on 0.5 (advects the wave normal).
        // Missing-texture fallback: opaque (B 1), neutral flow (G 0.5), no phase gradient (R 0).
        vec4 fm = (uHasMask == 1) ? texture(uMask, uv) : vec4(0.0, 0.5, 1.0, 1.0);
        float waterMask = fm.b;
        // Energy pulse travelling along the stream, driven by the R progress gradient.
        float pulse = 0.5 + 0.5 * sin((fm.r * 4.0 - uTime * uFlowSpeed * 3.0) * 6.2831853);
        vec2 fuv = uv * uFlowTile;
        vec2 flow = (fm.rg * 2.0 - 1.0) * uFlowStrength;
        float ph0 = fract(uTime * uFlowSpeed);
        float ph1 = fract(uTime * uFlowSpeed + 0.5);
        float bw = abs(ph0 - 0.5) * 2.0;                                  // ping-pong cross-fade weight
        // Travelling wave field (world-space) drives BOTH a view-independent caustic shimmer and the normal
        // that breaks the sun reflection into moving sparkles. Low frequency so it does not alias on the huge
        // map scale, and runs even when the wave-normal texture is missing.
        vec2 wc = vWorld.xz * 0.012;   // ~250-unit ripple wavelength (map world units)
        float wh = sin(wc.x * 2.0 + uTime * 1.5) + sin((wc.x + wc.y) * 1.4 - uTime * 1.1) + sin(wc.y * 2.6 - uTime * 0.9);
        vec2 waveXY = vec2(cos(wc.x * 2.0 + uTime * 1.5) + cos(wc.y * 2.6 - uTime * 0.9),
                           cos((wc.x + wc.y) * 1.4 - uTime * 1.1) - sin(wc.x * 2.0 + uTime * 1.5)) * 0.35;
        // Add the real Flowing_Normal_Map ripples when it loaded (uHasGradient). A MISSING normal is the white
        // fallback (1,1,1) whose 2*x-1 = (1,1) would tilt the whole surface and wash it out, so skip it then.
        if (uHasGradient == 1) {
            vec2 w0 = texture(uGradient, fuv + flow * ph0).xy * 2.0 - 1.0;
            vec2 w1 = texture(uGradient, fuv + flow * ph1).xy * 2.0 - 1.0;
            waveXY += mix(w0, w1, bw);
        }
        vec3 N = normalize(n + vec3(waveXY.x, 0.0, waveXY.y) * 0.5);
        vec3 V = viewDir;
        vec3 Hh = normalize(normalize(-uLight) + V);
        float ndh = max(dot(N, Hh), 0.0);
        // Deep(Inside) body tinting toward Outside at grazing edges, modulated by travelling caustic bands
        // (subtle brightness ripples), with a sharp sun sparkle (gated by the B specular mask) on the crests.
        float fres = pow(1.0 - max(dot(N, V), 0.0), 4.0);
        vec3 body = mix(uColorInside.rgb, uColorOutside.rgb, clamp(fres, 0.0, 1.0));
        float caustic = 0.5 + 0.5 * sin(wh * 1.6);
        float spark = pow(ndh, 90.0) * (0.3 + 0.7 * fm.r);   // reflection concentrated where R is bright
        // In game the river reads as DARK scene-lit water with bright streaks travelling along the R-gradient
        // lines, not a uniformly bright sheet. So: subtle caustic only (a strong 3-sine term reads as a dot
        // grid on big planes), and the animated highlight rides the R streaks in the Outside colour.
        vec3 colw = body * (0.9 + 0.1 * caustic) + uColorOutside.rgb * (fm.r * pulse * 0.7) + vec3(spark);
        // Sit the water in the baked scene lighting like every other lightmapped surface (night maps darken it).
        if (uHasLightmap == 1) colw *= bakedLightColour(texture(uLightmap, vLmUv).rgb * uLightmapScale);
        // The B mask cuts the water to its real stream shape (soft antialiased edges); TranslucentControl
        // sets how solid the visible water is. Sparkle reads a touch more solid.
        float a = clamp((uWaterAlpha * (0.75 + 0.25 * fres) + spark * 0.3) * waterMask, 0.0, 1.0);
        FragColor = vec4(colw, a);
        return;
    }

    float d = max(dot(n, normalize(-uLight)), 0.0);
    // Encode the fallback illumination the same way as BakedLight: it is a linear light term, so display
    // encode it before it modulates the already display-encoded diffuse. Without this, geometry with no
    // BakedLight UV (alpha decals, effects, props) is systematically darker than the encoded baked ground
    // it sits on - the M64/M65 encode only covered the lightmap path, leaving decals too dark.
    vec3 col = base * bakedLightColour(uSkyLight + uSunColor * d);

    // Baked lightmap: when the mesh carries a real BakedLight atlas, that IS the lighting for this
    // surface, so it replaces the fake directional term (finalColor = diffuse * lightmap * scale). The
    // scale is MapSunProperties.lightMapColorScale (2.0 on live Map12) - without it the map is too dark.
    if (uHasLightmap == 1) col = base * bakedLightColour(texture(uLightmap, vLmUv).rgb * uLightmapScale);

    // Specular highlight - computed only when the material's profile enables it (League materials are
    // diffuse/lambert by default). Blinn-Phong half-vector term.
    float specTerm = 0.0;
    if (uUsesSpec == 1) {
        vec3 h = normalize(normalize(-uLight) + viewDir);
        specTerm = pow(max(dot(n, h), 0.0), 32.0);
    }
    if (uMode == 9) { FragColor = vec4(vec3(specTerm), 1.0); return; }       // debug: specular only

    if (uMode == 1) {
        // RiotApprox. Fresnel rim (only if the material uses rim) coloured by its Gradient sampler and gated
        // by its Mask, + matcap, + emissive glow, + optional specular. (Cutout/alpha handled below per uAlphaMode.)
        if (uUsesRim == 1) {
            float fres = pow(1.0 - max(dot(n, viewDir), 0.0), 3.0);
            vec3 rimCol = (uHasGradient == 1)
                ? texture(uGradient, vec2(clamp(fres, 0.02, 0.98), 0.5)).rgb
                : (0.5 + 0.5 * base);
            float gate = (uHasMask == 1) ? mix(0.5, 1.0, texture(uMask, uv).r) : 1.0;
            col += fres * 0.6 * rimCol * gate;
        }
        if (uHasMatCap == 1 && uIsTerrainBlend == 0) {
            float mcGate = (uHasMatCapMask == 1) ? texture(uMatCapMask, uv).r : 1.0;
            col += matcapColour(n) * 0.6 * mcGate;
        }
        if (uHasEmissive == 1 && uIsTerrainBlend == 0) {
            float em = texture(uEmissive, uv).r;
            col += base * em * 1.5;
        }
        col += specTerm * 0.5;   // white-ish highlight, only when uUsesSpec
    }
    // M34 compositing: cutout discards low-alpha texels (opaque depth); transparent keeps the texture alpha
    // for the alpha-blend pass; opaque forces alpha 1. Applies to Basic (0) and RiotApprox (1).
    if ((uAlphaMode == 1 || uAlphaMode == 3) && alpha < uAlphaCutoff) discard;
    float outA = (uAlphaMode == 2 || uAlphaMode == 3) ? clamp(alpha, 0.0, 1.0) : 1.0;
    FragColor = vec4(col, outA);
}";

    private const string LineVert = @"
layout(location = 0) in vec3 aPos;
uniform mat4 uMvp;
void main() { gl_Position = uMvp * vec4(aPos, 1.0); }";

    private const string LineFrag = @"
out vec4 FragColor;
uniform vec4 uColor;
void main() { FragColor = uColor; }";

    public bool HasMesh => _hasMesh;
    public int SubmeshCount => _submeshes.Length;

    public unsafe void Initialize(GL gl, bool gles)
    {
        _gl = gl;
        _meshProgram = ShaderUtil.CreateProgram(gl, gles, MeshVert, MeshFrag);
        _mMvp = gl.GetUniformLocation(_meshProgram, "uMvp");
        _mModel = gl.GetUniformLocation(_meshProgram, "uModel");
        _mLight = gl.GetUniformLocation(_meshProgram, "uLight");
        _mSunColor = gl.GetUniformLocation(_meshProgram, "uSunColor");
        _mSkyLight = gl.GetUniformLocation(_meshProgram, "uSkyLight");
        _mTex = gl.GetUniformLocation(_meshProgram, "uTex");
        _mHasTex = gl.GetUniformLocation(_meshProgram, "uHasTex");
        _mBaseColor = gl.GetUniformLocation(_meshProgram, "uBaseColor");
        _mMode = gl.GetUniformLocation(_meshProgram, "uMode");
        _mCamPos = gl.GetUniformLocation(_meshProgram, "uCamPos");
        _mMask = gl.GetUniformLocation(_meshProgram, "uMask");
        _mGradient = gl.GetUniformLocation(_meshProgram, "uGradient");
        _mEmissive = gl.GetUniformLocation(_meshProgram, "uEmissive");
        _mHasMask = gl.GetUniformLocation(_meshProgram, "uHasMask");
        _mHasGradient = gl.GetUniformLocation(_meshProgram, "uHasGradient");
        _mHasEmissive = gl.GetUniformLocation(_meshProgram, "uHasEmissive");
        _mMatCap = gl.GetUniformLocation(_meshProgram, "uMatCap");
        _mMatCapMask = gl.GetUniformLocation(_meshProgram, "uMatCapMask");
        _mHasMatCap = gl.GetUniformLocation(_meshProgram, "uHasMatCap");
        _mHasMatCapMask = gl.GetUniformLocation(_meshProgram, "uHasMatCapMask");
        _mView = gl.GetUniformLocation(_meshProgram, "uView");
        _mUvScaleOffset = gl.GetUniformLocation(_meshProgram, "uUvScaleOffset");
        _mUvRot = gl.GetUniformLocation(_meshProgram, "uUvRot");
        _mUsesRim = gl.GetUniformLocation(_meshProgram, "uUsesRim");
        _mUsesSpec = gl.GetUniformLocation(_meshProgram, "uUsesSpec");
        _mHasVertexColor = gl.GetUniformLocation(_meshProgram, "uHasVertexColor");
        _mLightmap = gl.GetUniformLocation(_meshProgram, "uLightmap");
        _mHasLightmap = gl.GetUniformLocation(_meshProgram, "uHasLightmap");
        _mAlphaMode = gl.GetUniformLocation(_meshProgram, "uAlphaMode");
        _mTint = gl.GetUniformLocation(_meshProgram, "uTint");
        _mTintTextured = gl.GetUniformLocation(_meshProgram, "uTintTextured");
        _mAlphaCutoff = gl.GetUniformLocation(_meshProgram, "uAlphaCutoff");
        _mClampUv = gl.GetUniformLocation(_meshProgram, "uClampUv");
        _mTwoSided = gl.GetUniformLocation(_meshProgram, "uTwoSided");
        _mMirrored = gl.GetUniformLocation(_meshProgram, "uMirrored");
        _mIsFlowmap = gl.GetUniformLocation(_meshProgram, "uIsFlowmap");
        _mTime = gl.GetUniformLocation(_meshProgram, "uTime");
        _mFlowSpeed = gl.GetUniformLocation(_meshProgram, "uFlowSpeed");
        _mFlowStrength = gl.GetUniformLocation(_meshProgram, "uFlowStrength");
        _mFlowTile = gl.GetUniformLocation(_meshProgram, "uFlowTile");
        _mColorInside = gl.GetUniformLocation(_meshProgram, "uColorInside");
        _mColorOutside = gl.GetUniformLocation(_meshProgram, "uColorOutside");
        _mWaterAlpha = gl.GetUniformLocation(_meshProgram, "uWaterAlpha");
        _mIsTerrainBlend = gl.GetUniformLocation(_meshProgram, "uIsTerrainBlend");
        _mTerrainWorldScale = gl.GetUniformLocation(_meshProgram, "uTerrainWorldScale");
        _mTerrainBottomTiling = gl.GetUniformLocation(_meshProgram, "uTerrainBottomTiling");
        _mTerrainMiddleTiling = gl.GetUniformLocation(_meshProgram, "uTerrainMiddleTiling");
        _mTerrainTopTiling = gl.GetUniformLocation(_meshProgram, "uTerrainTopTiling");
        _mTerrainExtrasTiling = gl.GetUniformLocation(_meshProgram, "uTerrainExtrasTiling");
        _mTerrainMaskMultipliers = gl.GetUniformLocation(_meshProgram, "uTerrainMaskMultipliers");
        _mLightmapScale = gl.GetUniformLocation(_meshProgram, "uLightmapScale");

        _lineProgram = ShaderUtil.CreateProgram(gl, gles, LineVert, LineFrag);
        _lMvp = gl.GetUniformLocation(_lineProgram, "uMvp");
        _lColor = gl.GetUniformLocation(_lineProgram, "uColor");

        _whiteTex = MakeSolidTexture(255, 255, 255);

        _boundsVao = gl.GenVertexArray();
        _boundsVbo = gl.GenBuffer();
        _boneVao = gl.GenVertexArray();
        _boneVbo = gl.GenBuffer();
        _highlightVao = gl.GenVertexArray();
        _highlightVbo = gl.GenBuffer();
        _groupBoundsVao = gl.GenVertexArray();
        _groupBoundsVbo = gl.GenBuffer();
        _gizmoVao = gl.GenVertexArray();
        _gizmoVbo = gl.GenBuffer();
        _particleVao = gl.GenVertexArray();
        _particleVbo = gl.GenBuffer();
        _particleSelVao = gl.GenVertexArray();
        _particleSelVbo = gl.GenBuffer();
        _propVao = gl.GenVertexArray();
        _propVbo = gl.GenBuffer();
        _probeVao = gl.GenVertexArray();
        _probeVbo = gl.GenBuffer();
        _soundVao = gl.GenVertexArray();   // M55: sound placements
        _soundVbo = gl.GenBuffer();
        _bucketVao = gl.GenVertexArray();  // M55: bucket-grid overlay lines
        _bucketVbo = gl.GenBuffer();

        // M53: Unity-style placement ICONS — instanced camera-facing billboards (sparkle/person/ring
        // sprites, tinted per type) instead of "+" line crosses.
        _markerProgram = ShaderUtil.CreateProgram(gl, gles, MarkerVert, MarkerFrag);
        _mkViewProj = gl.GetUniformLocation(_markerProgram, "uViewProj");
        _mkCamRight = gl.GetUniformLocation(_markerProgram, "uCamRight");
        _mkCamUp = gl.GetUniformLocation(_markerProgram, "uCamUp");
        _mkSize = gl.GetUniformLocation(_markerProgram, "uSize");
        _mkColor = gl.GetUniformLocation(_markerProgram, "uColor");
        _mkTex = gl.GetUniformLocation(_markerProgram, "uTex");
        float[] quad = { -0.5f, -0.5f, 0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f };   // triangle strip
        _markerQuadVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _markerQuadVbo);
        fixed (float* q = quad)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), q, BufferUsageARB.StaticDraw);
        ConfigureMarkerVao(_particleVao, _particleVbo);
        ConfigureMarkerVao(_particleSelVao, _particleSelVbo);
        ConfigureMarkerVao(_propVao, _propVbo);
        ConfigureMarkerVao(_probeVao, _probeVbo);
        ConfigureMarkerVao(_soundVao, _soundVbo);
        _icoSparkle = UploadIcon(IconSparkle(48));
        _icoPerson = UploadIcon(IconPerson(48));
        _icoRing = UploadIcon(IconRing(48));
        _icoSpeaker = UploadIcon(IconSpeaker(48));

        _ready = true;
    }

    // ---- M53 placement icons ----
    private uint _markerProgram, _markerQuadVbo, _icoSparkle, _icoPerson, _icoRing, _icoSpeaker;
    private uint _soundVao, _soundVbo, _bucketVao, _bucketVbo;   // M55: sounds + bucket-grid overlay
    private int _soundVerts, _bucketVerts;
    private int _mkViewProj, _mkCamRight, _mkCamUp, _mkSize, _mkColor, _mkTex;
    private float _particleMarkerSize = 20f, _propMarkerSize = 20f, _probeMarkerSize = 20f, _particleSelSize = 20f, _soundMarkerSize = 20f;

    private unsafe void ConfigureMarkerVao(uint vao, uint instVbo)
    {
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _markerQuadVbo);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, instVbo);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        _gl.VertexAttribDivisor(1, 1);
        _gl.BindVertexArray(0);
    }

    private unsafe uint UploadIcon((byte[] Rgba, int N) icon)
    {
        var tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* p = icon.Rgba)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)icon.N, (uint)icon.N, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    /// <summary>4-point sparkle (particles): thin horizontal+vertical rays + a bright core.</summary>
    private static (byte[], int) IconSparkle(int n)
    {
        var px = new byte[n * n * 4];
        float c = (n - 1) / 2f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x - c) / c, v = (y - c) / c;
            float r = MathF.Sqrt(u * u + v * v);
            float rayH = MathF.Max(0f, 1f - MathF.Abs(v) * 7f) * MathF.Max(0f, 1f - MathF.Abs(u));
            float rayV = MathF.Max(0f, 1f - MathF.Abs(u) * 7f) * MathF.Max(0f, 1f - MathF.Abs(v));
            float core = MathF.Max(0f, 1f - r * 3.2f);
            float a = MathF.Min(1f, rayH + rayV + core * core);
            int o = (y * n + x) * 4;
            px[o] = px[o + 1] = px[o + 2] = 255;
            px[o + 3] = (byte)(a * 255f);
        }
        return (px, n);
    }

    /// <summary>Person silhouette (mobs/props): head circle over a rounded body.</summary>
    private static (byte[], int) IconPerson(int n)
    {
        var px = new byte[n * n * 4];
        float c = (n - 1) / 2f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x - c) / c, v = (y - c) / c;   // v: -1 top .. +1 bottom
            float dHead = MathF.Sqrt(u * u + (v + 0.42f) * (v + 0.42f));
            float head = MathF.Max(0f, 1f - MathF.Max(0f, dHead - 0.22f) * 14f);
            float bu = u / 0.34f, bv = (v - 0.28f) / 0.42f;
            float dBody = MathF.Sqrt(bu * bu + bv * bv);
            float body = MathF.Max(0f, 1f - MathF.Max(0f, dBody - 0.75f) * 8f);
            float a = MathF.Min(1f, head + body);
            int o = (y * n + x) * 4;
            px[o] = px[o + 1] = px[o + 2] = 255;
            px[o + 3] = (byte)(a * 255f);
        }
        return (px, n);
    }

    /// <summary>Speaker with sound waves (M55 sound placements).</summary>
    private static (byte[], int) IconSpeaker(int n)
    {
        var px = new byte[n * n * 4];
        float c = (n - 1) / 2f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x - c) / c, v = (y - c) / c;
            float a = 0f;
            // speaker body (small box) + cone (widening triangle)
            if (u >= -0.62f && u <= -0.30f && MathF.Abs(v) <= 0.20f) a = 1f;
            else if (u > -0.30f && u <= 0.05f)
            {
                float half = 0.20f + (u + 0.30f) * 0.9f;
                if (MathF.Abs(v) <= half) a = 1f;
            }
            // two sound-wave arcs to the right
            float r = MathF.Sqrt((u - 0.02f) * (u - 0.02f) + v * v);
            if (u > 0.1f && MathF.Abs(v) < r * 0.85f)
            {
                a = MathF.Max(a, MathF.Max(0f, 1f - MathF.Abs(r - 0.42f) * 16f));
                a = MathF.Max(a, MathF.Max(0f, 1f - MathF.Abs(r - 0.68f) * 16f));
            }
            int o = (y * n + x) * 4;
            px[o] = px[o + 1] = px[o + 2] = 255;
            px[o + 3] = (byte)(Math.Clamp(a, 0f, 1f) * 255f);
        }
        return (px, n);
    }

    /// <summary>Ring with a centre ball (reflection probes).</summary>
    private static (byte[], int) IconRing(int n)
    {
        var px = new byte[n * n * 4];
        float c = (n - 1) / 2f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x - c) / c, v = (y - c) / c;
            float r = MathF.Sqrt(u * u + v * v);
            float ring = MathF.Max(0f, 1f - MathF.Abs(r - 0.62f) * 12f);
            float ball = MathF.Max(0f, 1f - r * 3.4f);
            float a = MathF.Min(1f, ring + ball);
            int o = (y * n + x) * 4;
            px[o] = px[o + 1] = px[o + 2] = 255;
            px[o + 3] = (byte)(a * 255f);
        }
        return (px, n);
    }

    private const string MarkerVert = @"
layout(location=0) in vec2 aCorner;
layout(location=1) in vec3 aCenter;
uniform mat4 uViewProj;
uniform vec3 uCamRight;
uniform vec3 uCamUp;
uniform float uSize;
out vec2 vUv;
void main(){
    vec3 world = aCenter + uCamRight * (aCorner.x * uSize) + uCamUp * (aCorner.y * uSize);
    gl_Position = uViewProj * vec4(world, 1.0);
    vUv = aCorner + vec2(0.5, 0.5);
}";

    private const string MarkerFrag = @"
in vec2 vUv;
uniform sampler2D uTex;
uniform vec4 uColor;
out vec4 fragColor;
void main(){
    float a = texture(uTex, vUv).a * uColor.a;
    if (a < 0.02) discard;
    fragColor = vec4(uColor.rgb, a);
}";

    public unsafe void SetMesh(float[] positions, float[] normals, float[] uvs, uint[] indices,
        int vertexCount, Vector3 min, Vector3 max, IReadOnlyList<(int start, int count)> submeshes,
        float[]? colors = null, float[]? lightmapUvs = null)
    {
        if (!_ready) return;
        DeleteMeshBuffers();

        var interleaved = new float[vertexCount * 8];
        for (int i = 0; i < vertexCount; i++)
        {
            interleaved[i * 8 + 0] = positions[i * 3 + 0];
            interleaved[i * 8 + 1] = positions[i * 3 + 1];
            interleaved[i * 8 + 2] = positions[i * 3 + 2];
            interleaved[i * 8 + 3] = normals[i * 3 + 0];
            interleaved[i * 8 + 4] = normals[i * 3 + 1];
            interleaved[i * 8 + 5] = normals[i * 3 + 2];
            interleaved[i * 8 + 6] = uvs[i * 2 + 0];
            interleaved[i * 8 + 7] = uvs[i * 2 + 1];
        }
        _interleaved = interleaved;
        _meshVertexCount = vertexCount;

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = interleaved)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(interleaved.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        uint stride = 8 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        // Vertex color (location 3) lives in its own buffer so the stride-8 pos/normal/uv layout — and the
        // per-frame skinning that rewrites it — stay untouched. Absent → a constant white generic attribute.
        _hasVertexColor = colors is { Length: > 0 } && colors.Length >= vertexCount * 4;
        if (_hasVertexColor)
        {
            _colorVbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _colorVbo);
            fixed (float* cp = colors)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(colors!.Length * sizeof(float)), cp, BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(3);
            _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
        }
        else
        {
            _gl.DisableVertexAttribArray(3);
            _gl.VertexAttrib4(3, 1f, 1f, 1f, 1f); // constant white when the mesh has no PrimaryColor
        }

        // Baked lightmap UV (location 4) — same separate-VBO pattern; already atlas-mapped (uv7*scale+bias)
        // at decode. Absent → a constant (0,0) generic attribute (the lightmap is gated off per-submesh anyway).
        _hasLightmapUv = lightmapUvs is { Length: > 0 } && lightmapUvs.Length >= vertexCount * 2;
        if (_hasLightmapUv)
        {
            _lightmapUvVbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _lightmapUvVbo);
            fixed (float* lp = lightmapUvs)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(lightmapUvs!.Length * sizeof(float)), lp, BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(4);
            _gl.VertexAttribPointer(4, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        }
        else
        {
            _gl.DisableVertexAttribArray(4);
            _gl.VertexAttrib2(4, 0f, 0f);
        }

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
        _indexCount = indices.Length;

        var wire = new uint[indices.Length * 2];
        int w = 0;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            uint a = indices[t], b = indices[t + 1], c = indices[t + 2];
            wire[w++] = a; wire[w++] = b;
            wire[w++] = b; wire[w++] = c;
            wire[w++] = c; wire[w++] = a;
        }
        _wireEbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _wireEbo);
        fixed (uint* p = wire)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(wire.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
        _wireIndexCount = wire.Length;

        _gl.BindVertexArray(0);

        if (submeshes.Count == 0)
            _submeshes = new[] { SubmeshDraw.Create(0, indices.Length) };
        else
            _submeshes = submeshes.Select(s => SubmeshDraw.Create(s.start, s.count)).ToArray();

        UploadLines(_boundsVao, _boundsVbo, BuildBoxLines(min, max), out _boundsVerts);
        _hasMesh = true;
    }

    /// <summary>Uploads a texture once and returns its GL id (caller shares it across submeshes).</summary>
    public uint UploadTexture(byte[] rgba, int width, int height)
    {
        uint tex = UploadTextureInternal(rgba, width, height);
        _ownedTextures.Add(tex);
        return tex;
    }

    /// <summary>Upload a prop-mesh texture (M41): tracked separately so ClearProps frees it on map switch.</summary>
    public uint UploadPropTexture(byte[] rgba, int width, int height)
    {
        uint tex = UploadTextureInternal(rgba, width, height);
        _propTextures.Add(tex);
        return tex;
    }

    private unsafe uint UploadTextureInternal(byte[] rgba, int width, int height)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* p = rgba)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.GenerateMipmap(TextureTarget.Texture2D);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    /// <summary>Register a unique prop mesh's geometry (M41). Returns a handle to instance with
    /// <see cref="AddPropInstance"/>. Submeshes carry their diffuse texture id (0 = untextured).</summary>
    public unsafe int RegisterPropGeometry(float[] positions, float[] normals, float[] uvs, uint[] indices,
        IReadOnlyList<(int start, int count, uint tex)> submeshes)
    {
        if (!_ready) return -1;
        int vc = positions.Length / 3;
        var inter = new float[vc * 8];
        for (int i = 0; i < vc; i++)
        {
            int o = i * 8;
            inter[o] = positions[i * 3]; inter[o + 1] = positions[i * 3 + 1]; inter[o + 2] = positions[i * 3 + 2];
            inter[o + 3] = normals.Length >= (i * 3 + 3) ? normals[i * 3] : 0f;
            inter[o + 4] = normals.Length >= (i * 3 + 3) ? normals[i * 3 + 1] : 1f;
            inter[o + 5] = normals.Length >= (i * 3 + 3) ? normals[i * 3 + 2] : 0f;
            inter[o + 6] = uvs.Length >= (i * 2 + 2) ? uvs[i * 2] : 0f;
            inter[o + 7] = uvs.Length >= (i * 2 + 2) ? uvs[i * 2 + 1] : 0f;
        }

        var g = new PropGeometry { Interleaved = inter };
        g.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(g.Vao);
        g.Vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, g.Vbo);
        fixed (float* p = inter) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(inter.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        uint stride = 8 * sizeof(float);
        _gl.EnableVertexAttribArray(0); _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1); _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2); _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.DisableVertexAttribArray(3); _gl.VertexAttrib4(3, 1f, 1f, 1f, 1f);   // constant white vertex colour
        _gl.DisableVertexAttribArray(4); _gl.VertexAttrib2(4, 0f, 0f);           // no lightmap uv
        g.Ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, g.Ebo);
        fixed (uint* p = indices) _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
        _gl.BindVertexArray(0);

        g.Submeshes = submeshes.Select(s => (s.start, s.count, s.tex)).ToArray();
        _propGeoms.Add(g);
        return _propGeoms.Count - 1;
    }

    /// <summary>M54: replace a prop geometry's positions + normals (CPU-skinned idle frame); UVs kept.</summary>
    public unsafe void UpdatePropGeometryVertices(int geometry, float[] positions, float[] normals)
    {
        if (!_ready || geometry < 0 || geometry >= _propGeoms.Count) return;
        var g = _propGeoms[geometry];
        if (g.Interleaved is not { } inter) return;
        int vc = Math.Min(inter.Length / 8, positions.Length / 3);
        for (int i = 0; i < vc; i++)
        {
            int o = i * 8, s = i * 3;
            inter[o] = positions[s]; inter[o + 1] = positions[s + 1]; inter[o + 2] = positions[s + 2];
            if (s + 2 < normals.Length)
            { inter[o + 3] = normals[s]; inter[o + 4] = normals[s + 1]; inter[o + 5] = normals[s + 2]; }
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, g.Vbo);
        fixed (float* p = inter)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(vc * 8 * sizeof(float)), p);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <summary>Place a registered prop geometry at a world transform (M41).</summary>
    public void AddPropInstance(int geometry, Matrix4x4 model)
    {
        if (geometry >= 0 && geometry < _propGeoms.Count) _propMeshInstances.Add((geometry, model));
    }

    public int PropInstanceCount => _propMeshInstances.Count;

    /// <summary>Free all prop geometry + textures (M41) — call on map switch before re-registering.</summary>
    public void ClearProps()
    {
        if (!_ready) return;
        foreach (var g in _propGeoms) { _gl.DeleteVertexArray(g.Vao); _gl.DeleteBuffer(g.Vbo); _gl.DeleteBuffer(g.Ebo); }
        foreach (var t in _propTextures) _gl.DeleteTexture(t);
        _propGeoms.Clear();
        _propMeshInstances.Clear();
        _propTextures.Clear();
    }

    public void SetSubmeshTextureId(int index, uint textureId) => SetSubmeshLayer(index, 0, textureId);

    /// <summary>Show/hide a submesh (map dragon/baron layer filter). No-op outside range.</summary>
    public void SetSubmeshVisible(int index, bool visible)
    {
        if (!_ready || !_hasMesh || index < 0 || index >= _submeshes.Length) return;
        _submeshes[index].Visible = visible;
    }

    /// <summary>Set a submesh texture layer: 0 diffuse · 1 mask · 2 gradient · 3 emissive · 4 matcap ·
    /// 5 matcap-mask · 6 lightmap (0 = none).</summary>
    public void SetSubmeshLayer(int index, int slot, uint textureId)
    {
        if (!_ready || !_hasMesh || index < 0 || index >= _submeshes.Length) return;
        switch (slot)
        {
            case 0: _submeshes[index].Texture = textureId; break;
            case 1: _submeshes[index].Mask = textureId; break;
            case 2: _submeshes[index].Gradient = textureId; break;
            case 3: _submeshes[index].Emissive = textureId; break;
            case 4: _submeshes[index].MatCap = textureId; break;
            case 5: _submeshes[index].MatCapMask = textureId; break;
            case 6: _submeshes[index].Lightmap = textureId; break;
        }
    }

    /// <summary>Push a submesh's preview material (M32): UV transform + rim/specular feature flags.
    /// No-op outside range; unset submeshes keep the identity/off defaults (render exactly as before).</summary>
    /// <summary>M44: advance the shared animation clock (seconds). Drives flowmap river water; the App only
    /// needs to keep rendering frames (SetTime + RequestRender) while a flowmap submesh is present.</summary>
    public void SetTime(float seconds) => _time = seconds;

    /// <summary>M45: baked-lightmap multiplier from MapSunProperties.lightMapColorScale (1 = neutral).</summary>
    public void SetLightmapScale(float scale) => _lightmapScale = Math.Clamp(scale, 0.05f, 8f);

    /// <summary>Sets the authored map sun for surfaces that do not carry baked-light UVs.</summary>
    public void SetSunLighting(Vector3 directionToSun, Vector4 sunColor, Vector4 skyLightColor, float skyLightScale)
    {
        if (directionToSun.LengthSquared() < 1e-6f)
        {
            _lightDirection = new Vector3(-0.4f, -0.85f, -0.45f);
            _sunColor = new Vector3(0.75f);
            _skyLight = new Vector3(0.35f);
            return;
        }

        // The material shader's uLight is the ray direction, whereas MapSunProperties points toward the sun.
        _lightDirection = -Vector3.Normalize(directionToSun);
        _sunColor = Vector3.Clamp(new Vector3(sunColor.X, sunColor.Y, sunColor.Z), Vector3.Zero, new Vector3(8f));
        _skyLight = Vector3.Clamp(new Vector3(skyLightColor.X, skyLightColor.Y, skyLightColor.Z) *
            Math.Clamp(skyLightScale, 0f, 8f), Vector3.Zero, new Vector3(8f));
    }

    // M50b: selected submeshes render an accent wireframe OUTLINE overlay (replaces the AABB boxes).
    private IReadOnlyList<int>? _highlightSubmeshes;
    public void SetSubmeshHighlight(IReadOnlyList<int>? groupIndices) => _highlightSubmeshes = groupIndices;

    public void SetSubmeshMaterial(int index, SubmeshMaterial mat)
    {
        if (!_ready || !_hasMesh || index < 0 || index >= _submeshes.Length) return;
        _submeshes[index].UvScaleOffset = new Vector4(mat.UvScale.X, mat.UvScale.Y, mat.UvOffset.X, mat.UvOffset.Y);
        _submeshes[index].UvRotationRadians = mat.UvRotationDegrees * (MathF.PI / 180f);
        _submeshes[index].UsesRim = mat.UsesRim;
        _submeshes[index].UsesSpecular = mat.UsesSpecular;
        _submeshes[index].AlphaMode = mat.AlphaMode;
        _submeshes[index].AlphaCutoff = mat.AlphaCutoff;
        _submeshes[index].DoubleSided = mat.DoubleSided;
        _submeshes[index].Tint = mat.Tint ?? Vector4.One;
        _submeshes[index].TintTextured = mat.TintTextured;
        _submeshes[index].Mirrored = mat.Mirrored;
        _submeshes[index].ClampUv = new Vector2(mat.ClampU ? 1f : 0f, mat.ClampV ? 1f : 0f);
        _submeshes[index].IsFlowmap = mat.IsFlowmap;
        _submeshes[index].FlowSpeed = mat.FlowSpeed;
        _submeshes[index].FlowStrength = mat.FlowStrength;
        _submeshes[index].FlowTile = mat.FlowTile;
        _submeshes[index].ColorInside = mat.ColorInside;
        _submeshes[index].ColorOutside = mat.ColorOutside;
        _submeshes[index].WaterAlpha = mat.WaterAlpha;
        _submeshes[index].IsTerrainBlend = mat.IsTerrainBlend;
        _submeshes[index].TerrainBottomTiling = mat.TerrainBottomTiling;
        _submeshes[index].TerrainMiddleTiling = mat.TerrainMiddleTiling;
        _submeshes[index].TerrainTopTiling = mat.TerrainTopTiling;
        _submeshes[index].TerrainExtrasTiling = mat.TerrainExtrasTiling;
        _submeshes[index].TerrainWorldScale = mat.TerrainWorldScale;
        _submeshes[index].TerrainMaskMultipliers = mat.TerrainMaskMultipliers;
    }

    /// <summary>Reset every submesh's preview material to identity UV + no rim/specular (M32).</summary>
    public void ClearSubmeshMaterials()
    {
        for (int i = 0; i < _submeshes.Length; i++)
        {
            _submeshes[i].UvScaleOffset = new Vector4(1, 1, 0, 0);
            _submeshes[i].UvRotationRadians = 0f;
            _submeshes[i].UsesRim = false;
            _submeshes[i].UsesSpecular = false;
            _submeshes[i].AlphaMode = 0;
            _submeshes[i].AlphaCutoff = 0.35f;
            _submeshes[i].DoubleSided = false;
            _submeshes[i].Tint = Vector4.One;
            _submeshes[i].TintTextured = false;
            _submeshes[i].Mirrored = false;
            _submeshes[i].ClampUv = Vector2.Zero;
            _submeshes[i].IsFlowmap = false;
            _submeshes[i].IsTerrainBlend = false;
        }
    }

    /// <summary>Replace pos+normal of the existing mesh (keeps UVs/indices/textures) — for per-frame skinning.</summary>
    public unsafe void UpdateVertices(float[] positions, float[] normals)
    {
        if (!_ready || !_hasMesh || _interleaved is null) return;
        int vc = Math.Min(_meshVertexCount, Math.Min(positions.Length / 3, normals.Length / 3));
        for (int i = 0; i < vc; i++)
        {
            _interleaved[i * 8 + 0] = positions[i * 3 + 0];
            _interleaved[i * 8 + 1] = positions[i * 3 + 1];
            _interleaved[i * 8 + 2] = positions[i * 3 + 2];
            _interleaved[i * 8 + 3] = normals[i * 3 + 0];
            _interleaved[i * 8 + 4] = normals[i * 3 + 1];
            _interleaved[i * 8 + 5] = normals[i * 3 + 2];
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = _interleaved)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(_interleaved.Length * sizeof(float)), p);
    }

    public void SetBoneSegments(float[]? lineVerts)
    {
        if (!_ready) return;
        if (lineVerts is null || lineVerts.Length == 0) { _boneVerts = 0; return; }
        UploadLines(_boneVao, _boneVbo, lineVerts, out _boneVerts);
    }

    /// <summary>Draw a highlight wireframe box (always on top) around the given world-space bounds,
    /// or clear it when either bound is null (e.g. no map mesh selected).</summary>
    public void SetHighlightBounds(Vector3? min, Vector3? max)
    {
        if (min is { } a && max is { } b) SetHighlightBoxes(new[] { (a, b) });
        else SetHighlightBoxes(Array.Empty<(Vector3, Vector3)>());
    }

    /// <summary>Draw an amber highlight box (always on top) around EACH selected mesh's world bounds.</summary>
    public void SetHighlightBoxes(IReadOnlyList<(Vector3 min, Vector3 max)> boxes)
    {
        if (!_ready) return;
        if (boxes.Count == 0) { _highlightVerts = 0; return; }
        var verts = new float[boxes.Count * 12 * 2 * 3];
        int o = 0;
        foreach (var (min, max) in boxes)
        {
            var box = BuildBoxLines(min, max);
            Array.Copy(box, 0, verts, o, box.Length);
            o += box.Length;
        }
        UploadLines(_highlightVao, _highlightVbo, verts, out _highlightVerts);
    }

    /// <summary>Draw a dimmer box around the whole selection (the group bounds), or clear it.</summary>
    public void SetGroupBounds(Vector3? min, Vector3? max)
    {
        if (!_ready) return;
        if (min is not { } a || max is not { } b) { _groupBoundsVerts = 0; return; }
        UploadLines(_groupBoundsVao, _groupBoundsVbo, BuildBoxLines(a, b), out _groupBoundsVerts);
    }

    /// <summary>Set (or clear, with pivot=null) the transform gizmo (M42). <paramref name="mode"/> selects
    /// move (0, axis arrows) / rotate (1, rings) / scale (2, axis arms with box tips); the axis vectors let
    /// the gizmo follow world or local space.</summary>
    public void SetGizmo(Vector3? pivot, float armLength, int mode = 0, Vector3? ax = null, Vector3? ay = null, Vector3? az = null)
    {
        _hasGizmo = pivot.HasValue && armLength > 0f;
        if (pivot.HasValue) { _gizmoPivot = pivot.Value; _gizmoArmLength = armLength; }
        _gizmoMode = mode;
        _gizmoAxX = ax ?? Vector3.UnitX; _gizmoAxY = ay ?? Vector3.UnitY; _gizmoAxZ = az ?? Vector3.UnitZ;
    }

    /// <summary>Build the line-segment vertices for one gizmo axis handle in the given mode (M42).</summary>
    private static float[] BuildGizmoAxis(int mode, Vector3 pivot, Vector3 axis, float arm)
    {
        var v = new List<float>();
        void Seg(Vector3 a, Vector3 b) { v.Add(a.X); v.Add(a.Y); v.Add(a.Z); v.Add(b.X); v.Add(b.Y); v.Add(b.Z); }
        axis = Vector3.Normalize(axis);
        var u = Vector3.Normalize(MathF.Abs(axis.Y) < 0.99f ? Vector3.Cross(axis, Vector3.UnitY) : Vector3.Cross(axis, Vector3.UnitX));
        var w = Vector3.Cross(axis, u);

        if (mode == 1) // rotate: ring in the plane perpendicular to the axis
        {
            const int N = 48;
            Vector3 prev = pivot + u * arm;
            for (int i = 1; i <= N; i++)
            {
                float t = i / (float)N * MathF.Tau;
                var p = pivot + (u * MathF.Cos(t) + w * MathF.Sin(t)) * arm;
                Seg(prev, p); prev = p;
            }
        }
        else // move / scale: an arm from the pivot
        {
            var tip = pivot + axis * arm;
            Seg(pivot, tip);
            if (mode == 2) // scale: a small box at the tip
            {
                float s = arm * 0.06f;
                Vector3 C(int sx, int sy, int sz) => tip + axis * (sz * s) + u * (sx * s) + w * (sy * s);
                var c = new[] { C(-1, -1, -1), C(1, -1, -1), C(1, 1, -1), C(-1, 1, -1), C(-1, -1, 1), C(1, -1, 1), C(1, 1, 1), C(-1, 1, 1) };
                int[,] e = { { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 }, { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
                for (int i = 0; i < 12; i++) Seg(c[e[i, 0]], c[e[i, 1]]);
            }
        }
        return v.ToArray();
    }

    /// <summary>Set the placed-particle markers (M35/M53): an icon billboard at each world position, plus a
    /// larger highlighted one at the selected. An empty list clears them.</summary>
    public void SetParticleMarkers(IReadOnlyList<Vector3> positions, Vector3? selected, float size)
    {
        if (!_ready) return;
        _particleMarkerSize = size * 1.6f;
        SetIconMarkers(_particleVbo, positions, out _particleVerts);
        if (selected is { } sel)
        {
            _particleSelSize = size * 3.2f;
            SetIconMarkers(_particleSelVbo, new[] { sel }, out _particleSelVerts);
        }
        else _particleSelVerts = 0;
    }

    /// <summary>Set the animated-prop markers (M38/M53): a person icon at each placed character.</summary>
    public void SetPropMarkers(IReadOnlyList<Vector3> positions, float size)
    { _propMarkerSize = size * 1.6f; SetIconMarkers(_propVbo, positions, out _propVerts); }

    /// <summary>Set the cubemap-probe markers (M38/M53): a ring icon at each probe.</summary>
    public void SetProbeMarkers(IReadOnlyList<Vector3> positions, float size)
    { _probeMarkerSize = size * 1.6f; SetIconMarkers(_probeVbo, positions, out _probeVerts); }

    /// <summary>M55: sound placements — a speaker icon at each MapAudio position.</summary>
    public void SetSoundMarkers(IReadOnlyList<Vector3> positions, float size)
    { _soundMarkerSize = size * 1.6f; SetIconMarkers(_soundVbo, positions, out _soundVerts); }

    /// <summary>M55: bucket-grid overlay — world-space line list (pairs of xyz endpoints); null/empty clears.</summary>
    public void SetBucketGridLines(float[]? lineVerts)
    {
        if (!_ready) return;
        if (lineVerts is null || lineVerts.Length == 0) { _bucketVerts = 0; return; }
        UploadLines(_bucketVao, _bucketVbo, lineVerts, out _bucketVerts);
    }

    /// <summary>Upload marker instance centers (xyz per marker) for the icon billboards (M53).</summary>
    private unsafe void SetIconMarkers(uint instVbo, IReadOnlyList<Vector3> positions, out int count)
    {
        if (!_ready || positions.Count == 0) { count = 0; return; }
        var data = new float[positions.Count * 3];
        for (int i = 0; i < positions.Count; i++)
        {
            data[i * 3 + 0] = positions[i].X;
            data[i * 3 + 1] = positions[i].Y;
            data[i * 3 + 2] = positions[i].Z;
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, instVbo);
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        count = positions.Count;
    }

    public void ClearMesh()
    {
        DeleteMeshBuffers();
        _boneVerts = 0;
        _boundsVerts = 0;
        _highlightVerts = 0;
        _groupBoundsVerts = 0;
        _particleVerts = 0;
        _particleSelVerts = 0;
        _propVerts = 0;
        _probeVerts = 0;
        _soundVerts = 0;
        _bucketVerts = 0;
        _hasGizmo = false;
    }

    public unsafe void Render(Matrix4x4 viewProjection, bool wireframe, bool showBounds, bool showBones)
        => Render(viewProjection, Matrix4x4.Identity, Vector3.Zero, 0, wireframe, showBounds, showBones);

    public unsafe void Render(Matrix4x4 viewProjection, Vector3 camPos, int previewMode, bool wireframe, bool showBounds, bool showBones)
        => Render(viewProjection, Matrix4x4.Identity, camPos, previewMode, wireframe, showBounds, showBones);

    public unsafe void Render(Matrix4x4 viewProjection, Matrix4x4 view, Vector3 camPos, int previewMode, bool wireframe, bool showBounds, bool showBones, bool cullBackfaces = false)
    {
        if (!_ready) return;
        var m = viewProjection;

        if (_hasMesh)
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.Disable(EnableCap.CullFace);
            // FrontFace=CW is the verified winding for THIS pipeline: the viewport mirrors world X
            // (CreateScale(-1,1,1)), which flips triangle orientation once, so front faces land clockwise in
            // window space. Confirmed by headless-rendering base_srx through the exact mirror+view/proj the app
            // uses (CCW culls the ground away; CW keeps the full map). Set it ALWAYS (not just when culling) so
            // gl_FrontFacing is meaningful for two-sided lighting; cull the back faces.
            _gl.FrontFace(FrontFaceDirection.CW);
            _gl.CullFace(TriangleFace.Back);
            _gl.BindVertexArray(_vao);

            if (wireframe)
            {
                _gl.UseProgram(_lineProgram);
                _gl.UniformMatrix4(_lMvp, 1, false, in m.M11);
                _gl.Uniform4(_lColor, 0.55f, 0.62f, 0.72f, 1f);
                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _wireEbo);
                _gl.DrawElements(PrimitiveType.Lines, (uint)_wireIndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }
            else
            {
                _gl.UseProgram(_meshProgram);
                _gl.UniformMatrix4(_mMvp, 1, false, in m.M11);
                var model = Matrix4x4.Identity;
                _gl.UniformMatrix4(_mModel, 1, false, in model.M11);
                _gl.Uniform3(_mLight, _lightDirection.X, _lightDirection.Y, _lightDirection.Z);
                _gl.Uniform3(_mSunColor, _sunColor.X, _sunColor.Y, _sunColor.Z);
                _gl.Uniform3(_mSkyLight, _skyLight.X, _skyLight.Y, _skyLight.Z);
                _gl.Uniform3(_mBaseColor, 0.62f, 0.66f, 0.74f);
                _gl.Uniform1(_mMode, previewMode);
                _gl.Uniform1(_mHasVertexColor, _hasVertexColor ? 1 : 0);
                _gl.Uniform3(_mCamPos, camPos.X, camPos.Y, camPos.Z);
                _gl.UniformMatrix4(_mView, 1, false, in view.M11);
                _gl.Uniform1(_mTex, 0);
                _gl.Uniform1(_mMask, 1);
                _gl.Uniform1(_mGradient, 2);
                _gl.Uniform1(_mEmissive, 3);
                _gl.Uniform1(_mMatCap, 4);
                _gl.Uniform1(_mMatCapMask, 5);
                _gl.Uniform1(_mLightmap, 6);
                _gl.Uniform1(_mTime, _time);   // M44: shared animation clock (flowmap water)
                _gl.Uniform1(_mLightmapScale, _lightmapScale);   // M45: baked-light multiplier
                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);

                void DrawSubmesh(SubmeshDraw s)
                {
                    // M34 render state: cull the back faces of single-sided (cullEnable=true) materials; render
                    // two-sided (cullEnable=false) materials both sides. The master toggle can force everything
                    // two-sided (cullBackfaces=false). A face renders two-sided exactly when it is NOT culled,
                    // and then the shader flips its backface normals so they light correctly.
                    bool cull = cullBackfaces && !s.DoubleSided;
                    if (cull) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
                    _gl.Uniform1(_mTwoSided, cull ? 0 : 1);
                    _gl.Uniform1(_mMirrored, s.Mirrored ? 1 : 0);
                    // M32 per-material: UV transform + rim/specular gates (identity/off by default).
                    _gl.Uniform4(_mUvScaleOffset, s.UvScaleOffset.X, s.UvScaleOffset.Y, s.UvScaleOffset.Z, s.UvScaleOffset.W);
                    _gl.Uniform1(_mUvRot, s.UvRotationRadians);
                    _gl.Uniform1(_mUsesRim, s.UsesRim ? 1 : 0);
                    _gl.Uniform1(_mUsesSpec, s.UsesSpecular ? 1 : 0);
                    _gl.Uniform1(_mAlphaMode, s.AlphaMode);   // M34: 0 opaque · 1 cutout · 2 transparent
                    _gl.Uniform1(_mAlphaCutoff, s.AlphaCutoff);
                    _gl.Uniform2(_mClampUv, s.ClampUv.X, s.ClampUv.Y);
                    _gl.Uniform4(_mTint, s.Tint.X, s.Tint.Y, s.Tint.Z, s.Tint.W);
                    _gl.Uniform1(_mTintTextured, s.TintTextured ? 1 : 0);
                    // M44 flowmap river water: animated flowing water (Flow_Map on slot 1, normal on slot 2).
                    _gl.Uniform1(_mIsFlowmap, s.IsFlowmap ? 1 : 0);
                    if (s.IsFlowmap)
                    {
                        _gl.Uniform1(_mFlowSpeed, s.FlowSpeed);
                        _gl.Uniform1(_mFlowStrength, s.FlowStrength);
                        _gl.Uniform2(_mFlowTile, s.FlowTile.X, s.FlowTile.Y);
                        _gl.Uniform4(_mColorInside, s.ColorInside.X, s.ColorInside.Y, s.ColorInside.Z, s.ColorInside.W);
                        _gl.Uniform4(_mColorOutside, s.ColorOutside.X, s.ColorOutside.Y, s.ColorOutside.Z, s.ColorOutside.W);
                        _gl.Uniform1(_mWaterAlpha, s.WaterAlpha);
                    }
                    _gl.Uniform1(_mIsTerrainBlend, s.IsTerrainBlend ? 1 : 0);
                    if (s.IsTerrainBlend)
                    {
                        _gl.Uniform1(_mTerrainWorldScale, s.TerrainWorldScale);
                        _gl.Uniform2(_mTerrainBottomTiling, s.TerrainBottomTiling.X, s.TerrainBottomTiling.Y);
                        _gl.Uniform2(_mTerrainMiddleTiling, s.TerrainMiddleTiling.X, s.TerrainMiddleTiling.Y);
                        _gl.Uniform2(_mTerrainTopTiling, s.TerrainTopTiling.X, s.TerrainTopTiling.Y);
                        _gl.Uniform2(_mTerrainExtrasTiling, s.TerrainExtrasTiling.X, s.TerrainExtrasTiling.Y);
                        _gl.Uniform3(_mTerrainMaskMultipliers, s.TerrainMaskMultipliers.X,
                            s.TerrainMaskMultipliers.Y, s.TerrainMaskMultipliers.Z);
                    }
                    _gl.ActiveTexture(TextureUnit.Texture0);
                    _gl.BindTexture(TextureTarget.Texture2D, s.Texture != 0 ? s.Texture : _whiteTex);
                    _gl.Uniform1(_mHasTex, s.Texture != 0 ? 1 : 0);
                    _gl.ActiveTexture(TextureUnit.Texture1);
                    _gl.BindTexture(TextureTarget.Texture2D, s.Mask != 0 ? s.Mask : _whiteTex);
                    _gl.Uniform1(_mHasMask, s.Mask != 0 ? 1 : 0);
                    _gl.ActiveTexture(TextureUnit.Texture2);
                    _gl.BindTexture(TextureTarget.Texture2D, s.Gradient != 0 ? s.Gradient : _whiteTex);
                    _gl.Uniform1(_mHasGradient, s.Gradient != 0 ? 1 : 0);
                    _gl.ActiveTexture(TextureUnit.Texture3);
                    _gl.BindTexture(TextureTarget.Texture2D, s.Emissive != 0 ? s.Emissive : _whiteTex);
                    _gl.Uniform1(_mHasEmissive, s.Emissive != 0 ? 1 : 0);
                    _gl.ActiveTexture(TextureUnit.Texture4);
                    _gl.BindTexture(TextureTarget.Texture2D, s.MatCap != 0 ? s.MatCap : _whiteTex);
                    _gl.Uniform1(_mHasMatCap, s.MatCap != 0 ? 1 : 0);
                    _gl.ActiveTexture(TextureUnit.Texture5);
                    _gl.BindTexture(TextureTarget.Texture2D, s.MatCapMask != 0 ? s.MatCapMask : _whiteTex);
                    _gl.Uniform1(_mHasMatCapMask, s.MatCapMask != 0 ? 1 : 0);
                    _gl.ActiveTexture(TextureUnit.Texture6);
                    _gl.BindTexture(TextureTarget.Texture2D, s.Lightmap != 0 ? s.Lightmap : _whiteTex);
                    _gl.Uniform1(_mHasLightmap, (s.Lightmap != 0 && _hasLightmapUv) ? 1 : 0);
                    _gl.DrawElements(PrimitiveType.Triangles, (uint)s.Count, DrawElementsType.UnsignedInt, (void*)(s.Start * sizeof(uint)));
                }

                // Pass 1: opaque + cutout (AlphaMode 0/1) with depth writes on.
                foreach (var s in _submeshes)
                    if (s.Visible && s.AlphaMode < 2) DrawSubmesh(s);

                // Pass 2: transparent modes (2/3) after solids — alpha-blend, depth-test on but NO depth
                // write, so overlapping glass/water composites without occluding itself. (No back-to-front
                // sort — acceptable for a preview.)
                bool anyTransparent = false;
                foreach (var s in _submeshes) if (s.Visible && s.AlphaMode >= 2) { anyTransparent = true; break; }
                if (anyTransparent)
                {
                    _gl.Enable(EnableCap.Blend);
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    _gl.DepthMask(false);
                    foreach (var s in _submeshes)
                        if (s.Visible && s.AlphaMode >= 2) DrawSubmesh(s);
                    _gl.DepthMask(true);
                }
                _gl.Disable(EnableCap.CullFace); // restore default so the line/gizmo overlays are unaffected

                // M50b: selection OUTLINE — draw the selected submeshes' wireframe (from the prebuilt
                // per-triangle line EBO: triangle t occupies wire indices [t*6, t*6+6), so a submesh's
                // index range [Start, Start+Count) maps to wire range [Start*2, Count*2)). GLES-safe —
                // no glPolygonMode (ANGLE lacks it; using it crashed on select). LEQUAL so the lines,
                // which share the mesh's exact vertices, pass the depth test on the visible surface.
                if (_highlightSubmeshes is { Count: > 0 } hl)
                {
                    _gl.UseProgram(_lineProgram);
                    _gl.UniformMatrix4(_lMvp, 1, false, in m.M11);
                    _gl.Uniform4(_lColor, 0.21f, 0.89f, 0.76f, 1f);   // Kalista accent
                    _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _wireEbo);
                    _gl.DepthFunc(DepthFunction.Lequal);
                    foreach (var gi in hl)
                        if (gi >= 0 && gi < _submeshes.Length && _submeshes[gi].Visible)
                            _gl.DrawElements(PrimitiveType.Lines, (uint)(_submeshes[gi].Count * 2),
                                DrawElementsType.UnsignedInt, (void*)((long)_submeshes[gi].Start * 2 * sizeof(uint)));
                    _gl.DepthFunc(DepthFunction.Less);
                    _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
                }
            }
            _gl.BindVertexArray(0);
        }

        // M41: placed prop meshes (SRU_Baron, dragons, jungle camps…) — each unique geometry instanced at its
        // world transform. Basic lit + diffuse only (props don't use the map's mask/gradient/emissive/lightmap).
        if (_propMeshInstances.Count > 0 && !wireframe)
        {
            _gl.UseProgram(_meshProgram);
            _gl.Uniform3(_mLight, -0.4f, -0.85f, -0.45f);
            _gl.Uniform3(_mBaseColor, 0.62f, 0.66f, 0.74f);
            _gl.Uniform1(_mMode, previewMode);
            _gl.Uniform1(_mHasVertexColor, 0);
            _gl.Uniform3(_mCamPos, camPos.X, camPos.Y, camPos.Z);
            _gl.UniformMatrix4(_mView, 1, false, in view.M11);
            _gl.Uniform1(_mTex, 0); _gl.Uniform1(_mMask, 1); _gl.Uniform1(_mGradient, 2);
            _gl.Uniform1(_mEmissive, 3); _gl.Uniform1(_mMatCap, 4); _gl.Uniform1(_mMatCapMask, 5); _gl.Uniform1(_mLightmap, 6);
            _gl.Uniform1(_mHasMask, 0); _gl.Uniform1(_mHasGradient, 0); _gl.Uniform1(_mHasEmissive, 0);
            _gl.Uniform1(_mHasMatCap, 0); _gl.Uniform1(_mHasMatCapMask, 0); _gl.Uniform1(_mHasLightmap, 0);
            _gl.Uniform1(_mUsesRim, 0); _gl.Uniform1(_mUsesSpec, 0);
            _gl.Uniform1(_mAlphaMode, 1); _gl.Uniform1(_mAlphaCutoff, 0.35f);   // cutout so fur/wing alpha reads
            _gl.Uniform4(_mUvScaleOffset, 1f, 1f, 0f, 0f); _gl.Uniform1(_mUvRot, 0f);
            _gl.Uniform2(_mClampUv, 0f, 0f); _gl.Uniform4(_mTint, 1f, 1f, 1f, 1f); _gl.Uniform1(_mTintTextured, 0); _gl.Uniform1(_mMirrored, 0);
            _gl.Uniform1(_mIsFlowmap, 0);   // M44: props never flow
            for (int u = 1; u <= 6; u++) { _gl.ActiveTexture(TextureUnit.Texture0 + u); _gl.BindTexture(TextureTarget.Texture2D, _whiteTex); }

            _gl.Enable(EnableCap.DepthTest);
            bool propCull = cullBackfaces;
            if (propCull) { _gl.Enable(EnableCap.CullFace); _gl.CullFace(TriangleFace.Back); } else _gl.Disable(EnableCap.CullFace);
            _gl.Uniform1(_mTwoSided, propCull ? 0 : 1);

            foreach (var (geo, model) in _propMeshInstances)
            {
                var mvp = model * m;
                _gl.UniformMatrix4(_mMvp, 1, false, in mvp.M11);
                _gl.UniformMatrix4(_mModel, 1, false, in model.M11);
                // the -X mirror flips winding once; a mirrored placement (negative determinant) flips it back,
                // so pick the front-face winding per instance to keep culling + two-sided lighting correct.
                _gl.FrontFace(model.GetDeterminant() < 0 ? FrontFaceDirection.Ccw : FrontFaceDirection.CW);

                var g = _propGeoms[geo];
                _gl.BindVertexArray(g.Vao);
                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, g.Ebo);
                foreach (var (start, count, tex) in g.Submeshes)
                {
                    _gl.ActiveTexture(TextureUnit.Texture0);
                    _gl.BindTexture(TextureTarget.Texture2D, tex != 0 ? tex : _whiteTex);
                    _gl.Uniform1(_mHasTex, tex != 0 ? 1 : 0);
                    _gl.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedInt, (void*)(start * sizeof(uint)));
                }
            }
            _gl.Disable(EnableCap.CullFace);
            _gl.BindVertexArray(0);
        }

        if ((showBounds && _boundsVerts > 0) || (showBones && _boneVerts > 0))
        {
            _gl.UseProgram(_lineProgram);
            _gl.UniformMatrix4(_lMvp, 1, false, in m.M11);

            if (showBounds && _boundsVerts > 0)
            {
                _gl.Uniform4(_lColor, 0.36f, 0.89f, 0.76f, 1f);
                _gl.BindVertexArray(_boundsVao);
                _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_boundsVerts);
            }
            if (showBones && _boneVerts > 0)
            {
                _gl.Disable(EnableCap.DepthTest);
                _gl.Uniform4(_lColor, 1.0f, 0.65f, 0.2f, 1f);
                _gl.BindVertexArray(_boneVao);
                _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_boneVerts);
            }
            _gl.BindVertexArray(0);
        }

        // Selection highlight: a bright box around each selected mesh + a dimmer box around the whole
        // group, always on top so they read clearly even inside dense geometry.
        if (_highlightVerts > 0 || _groupBoundsVerts > 0)
        {
            _gl.UseProgram(_lineProgram);
            _gl.UniformMatrix4(_lMvp, 1, false, in m.M11);
            _gl.Disable(EnableCap.DepthTest);
            if (_groupBoundsVerts > 0)
            {
                _gl.Uniform4(_lColor, 0.5f, 0.65f, 0.95f, 1f); // group bounds — cool blue
                _gl.BindVertexArray(_groupBoundsVao);
                _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_groupBoundsVerts);
            }
            if (_highlightVerts > 0)
            {
                _gl.Uniform4(_lColor, 1.0f, 0.78f, 0.2f, 1f); // selection amber
                _gl.BindVertexArray(_highlightVao);
                _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_highlightVerts);
            }
            _gl.BindVertexArray(0);
        }

        // Transform gizmo (M42): move arrows / rotate rings / scale arms, along the selected mesh's axes
        // from its pivot (X=red, Y=green, Z=blue), always on top so it stays clickable.
        if (_hasGizmo)
        {
            var xg = BuildGizmoAxis(_gizmoMode, _gizmoPivot, _gizmoAxX, _gizmoArmLength);
            var yg = BuildGizmoAxis(_gizmoMode, _gizmoPivot, _gizmoAxY, _gizmoArmLength);
            var zg = BuildGizmoAxis(_gizmoMode, _gizmoPivot, _gizmoAxZ, _gizmoArmLength);
            var all = new float[xg.Length + yg.Length + zg.Length];
            System.Array.Copy(xg, 0, all, 0, xg.Length);
            System.Array.Copy(yg, 0, all, xg.Length, yg.Length);
            System.Array.Copy(zg, 0, all, xg.Length + yg.Length, zg.Length);

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _gizmoVbo);
            unsafe { fixed (float* p = all) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(all.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw); }
            _gl.BindVertexArray(_gizmoVao);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

            _gl.UseProgram(_lineProgram);
            _gl.UniformMatrix4(_lMvp, 1, false, in m.M11);
            _gl.Disable(EnableCap.DepthTest);
            _gl.LineWidth(2.5f);
            int xN = xg.Length / 3, yN = yg.Length / 3, zN = zg.Length / 3;
            _gl.Uniform4(_lColor, 0.95f, 0.25f, 0.25f, 1f); _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)xN);         // X red
            _gl.Uniform4(_lColor, 0.3f, 0.9f, 0.35f, 1f); _gl.DrawArrays(PrimitiveType.Lines, xN, (uint)yN);          // Y green
            _gl.Uniform4(_lColor, 0.3f, 0.55f, 0.98f, 1f); _gl.DrawArrays(PrimitiveType.Lines, xN + yN, (uint)zN);    // Z blue
            _gl.LineWidth(1f);
            _gl.BindVertexArray(0);
        }

        // M55: bucket-grid overlay (depth-tested violet lines on the ground plane)
        if (_bucketVerts > 0)
        {
            _gl.UseProgram(_lineProgram);
            _gl.UniformMatrix4(_lMvp, 1, false, in m.M11);
            _gl.Uniform4(_lColor, 0.62f, 0.45f, 0.95f, 1f);
            _gl.BindVertexArray(_bucketVao);
            _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_bucketVerts);
            _gl.BindVertexArray(0);
        }

        // M53: placement ICONS (Unity-style camera-facing sprites), always on top: particles = cyan
        // sparkle, mobs/props = orange person, probes = green ring, sounds = violet speaker;
        // selected = larger bright yellow.
        if (_particleVerts > 0 || _particleSelVerts > 0 || _propVerts > 0 || _probeVerts > 0 || _soundVerts > 0)
        {
            Matrix4x4.Invert(view, out var invView);
            var camRight = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, invView));
            var camUp = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, invView));

            _gl.UseProgram(_markerProgram);
            _gl.UniformMatrix4(_mkViewProj, 1, false, in m.M11);
            _gl.Uniform3(_mkCamRight, camRight.X, camRight.Y, camRight.Z);
            _gl.Uniform3(_mkCamUp, camUp.X, camUp.Y, camUp.Z);
            _gl.Uniform1(_mkTex, 0);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.Disable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            DrawIconSet(_particleVao, _particleVerts, _icoSparkle, _particleMarkerSize, 0.30f, 0.89f, 0.95f, 0.85f);
            DrawIconSet(_propVao, _propVerts, _icoPerson, _propMarkerSize, 1.00f, 0.58f, 0.18f, 0.9f);
            DrawIconSet(_probeVao, _probeVerts, _icoRing, _probeMarkerSize, 0.32f, 0.93f, 0.47f, 0.9f);
            DrawIconSet(_soundVao, _soundVerts, _icoSpeaker, _soundMarkerSize, 0.78f, 0.55f, 1.0f, 0.9f);
            DrawIconSet(_particleSelVao, _particleSelVerts, _icoSparkle, _particleSelSize, 1.0f, 0.92f, 0.25f, 1f);

            _gl.Disable(EnableCap.Blend);
            _gl.BindVertexArray(0);
        }
    }

    private unsafe void DrawIconSet(uint vao, int count, uint icon, float size, float r, float g, float b, float a)
    {
        if (count == 0) return;
        _gl.Uniform4(_mkColor, r, g, b, a);
        _gl.Uniform1(_mkSize, size);
        _gl.BindTexture(TextureTarget.Texture2D, icon);
        _gl.BindVertexArray(vao);
        _gl.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, (uint)count);
    }

    private unsafe uint MakeSolidTexture(byte r, byte g, byte b)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        byte[] px = { r, g, b, 255 };
        fixed (byte* p = px)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private unsafe void UploadLines(uint vao, uint vbo, float[] verts, out int vertexCount)
    {
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = verts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)(3 * sizeof(float)), (void*)0);
        _gl.BindVertexArray(0);
        vertexCount = verts.Length / 3;
    }

    private static float[] BuildBoxLines(Vector3 a, Vector3 b)
    {
        Vector3[] c =
        {
            new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, a.Y, b.Z), new(a.X, a.Y, b.Z),
            new(a.X, b.Y, a.Z), new(b.X, b.Y, a.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z),
        };
        int[,] edges = { {0,1},{1,2},{2,3},{3,0}, {4,5},{5,6},{6,7},{7,4}, {0,4},{1,5},{2,6},{3,7} };
        var list = new float[edges.GetLength(0) * 2 * 3];
        int k = 0;
        for (int e = 0; e < edges.GetLength(0); e++)
            foreach (int idx in new[] { edges[e, 0], edges[e, 1] })
            {
                list[k++] = c[idx].X; list[k++] = c[idx].Y; list[k++] = c[idx].Z;
            }
        return list;
    }

    private void DeleteMeshBuffers()
    {
        if (!_hasMesh) return;
        foreach (var t in _ownedTextures) _gl.DeleteTexture(t);
        _ownedTextures.Clear();
        _submeshes = Array.Empty<SubmeshDraw>();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteBuffer(_wireEbo);
        if (_colorVbo != 0) { _gl.DeleteBuffer(_colorVbo); _colorVbo = 0; }
        if (_lightmapUvVbo != 0) { _gl.DeleteBuffer(_lightmapUvVbo); _lightmapUvVbo = 0; }
        _gl.DeleteVertexArray(_vao);
        _hasVertexColor = false;
        _hasLightmapUv = false;
        _hasMesh = false;
    }

    public void Dispose()
    {
        if (!_ready) return;
        DeleteMeshBuffers();
        _gl.DeleteTexture(_whiteTex);
        _gl.DeleteBuffer(_boundsVbo);
        _gl.DeleteBuffer(_boneVbo);
        _gl.DeleteBuffer(_particleVbo);
        _gl.DeleteBuffer(_particleSelVbo);
        _gl.DeleteBuffer(_propVbo);
        _gl.DeleteBuffer(_probeVbo);
        _gl.DeleteVertexArray(_boundsVao);
        _gl.DeleteVertexArray(_boneVao);
        _gl.DeleteVertexArray(_particleVao);
        _gl.DeleteVertexArray(_particleSelVao);
        _gl.DeleteVertexArray(_propVao);
        _gl.DeleteVertexArray(_probeVao);
        ClearProps();
        _gl.DeleteProgram(_meshProgram);
        _gl.DeleteProgram(_lineProgram);
        _ready = false;
    }
}
