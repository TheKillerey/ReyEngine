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
    private int _mMvp, _mModel, _mLight, _mTex, _mHasTex, _mBaseColor, _mMode, _mCamPos;
    private int _mMask, _mGradient, _mEmissive, _mHasMask, _mHasGradient, _mHasEmissive;
    private int _mMatCap, _mMatCapMask, _mHasMatCap, _mHasMatCapMask, _mView;
    private int _mUvScaleOffset, _mUvRot, _mUsesRim, _mUsesSpec;   // M32: per-material UV + feature flags
    private int _mHasVertexColor;                                  // M33: mapgeo PrimaryColor present
    private int _mLightmap, _mHasLightmap;                         // M33: baked lightmap atlas (slot 6, Texcoord7 UV)
    private int _mAlphaMode;                                       // M34: 0 opaque · 1 cutout · 2 transparent
    private int _mTint;                                            // M34: TintColor for untextured effect materials
    private int _mAlphaCutoff;                                     // M34: per-material alpha-test threshold (AlphaTestValue)
    private int _mClampUv;                                         // M34: per-axis UV clamp (decals; addressU/V == Clamp)
    private int _mTwoSided, _mMirrored;                            // M34: two-sided lighting + mirrored-transform debug
    private int _lMvp, _lColor;

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
    private sealed class PropGeometry { public uint Vao, Vbo, Ebo; public (int Start, int Count, uint Tex)[] Submeshes = System.Array.Empty<(int, int, uint)>(); }
    private readonly List<PropGeometry> _propGeoms = new();
    private readonly List<(int Geo, Matrix4x4 Model)> _propMeshInstances = new();
    private readonly List<uint> _propTextures = new();
    private bool _hasGizmo;
    private Vector3 _gizmoPivot;
    private float _gizmoArmLength;

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
        public int AlphaMode;           // M34: 0 opaque, 1 cutout (alpha-test), 2 transparent (alpha-blend)
        public float AlphaCutoff;       // M34: alpha-test threshold for cutout (default 0.35)
        public bool DoubleSided;        // M34: two-sided material (cullEnable=false) — never culled
        public bool Mirrored;           // M34: source mesh has a negative-determinant (mirrored) transform
        public Vector2 ClampUv;         // M34: per-axis UV clamp (1 = clamp/decal, 0 = tile); default 0,0
        public Vector4 Tint;            // M34: TintColor, applied to the UNTEXTURED fallback only; default 1,1,1,1

        public static SubmeshDraw Create(int start, int count) =>
            new() { Start = start, Count = count, Visible = true, UvScaleOffset = new Vector4(1, 1, 0, 0), Tint = Vector4.One, AlphaCutoff = 0.35f };
    }

    /// <summary>Per-submesh preview material data pushed from the App's resolved <c>MaterialProfile</c> (M32/M34).
    /// <paramref name="Mirrored"/> is a per-mesh geometric flag (negative-determinant transform), not a material one.</summary>
    public readonly record struct SubmeshMaterial(
        bool UsesRim, bool UsesSpecular, Vector2 UvScale, Vector2 UvOffset, float UvRotationDegrees,
        int AlphaMode = 0, bool DoubleSided = false, Vector4? Tint = null, bool Mirrored = false, float AlphaCutoff = 0.35f,
        bool ClampU = false, bool ClampV = false)
    {
        public static readonly SubmeshMaterial Default = new(false, false, Vector2.One, Vector2.Zero, 0f, 0, false, null, false, 0.35f, false, false);
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
uniform int uAlphaMode;        // M34: 0 opaque, 1 cutout (alpha-test discard), 2 transparent (alpha-blend)
uniform float uAlphaCutoff;    // M34: alpha-test threshold for cutout mode (from AlphaTestValue; default 0.35)
uniform vec2 uClampUv;         // M34: per-axis UV clamp (1 = clamp to [0,1] for decals; 0 = tile)
uniform vec4 uTint;            // M34: TintColor (rgba) for UNTEXTURED effect materials; 1,1,1,1 = none
uniform int uTwoSided;         // M34: 1 when this face renders two-sided (flip backface normals for lighting)
uniform int uMirrored;         // M34: 1 when the source mesh transform is mirrored (negative determinant)

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
    // Textured materials sample the diffuse untouched (uTint never affects them). Untextured effect/indicator
    // materials (FaeLights etc.) use their TintColor as the colour + alpha instead of the plain grey fallback.
    vec4 tex = (uHasTex == 1) ? texture(uTex, uv) : vec4(uBaseColor * uTint.rgb, uTint.a);
    vec3 base = tex.rgb;
    float alpha = tex.a;

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
    float d = max(dot(n, normalize(-uLight)), 0.0);
    float light = 0.35 + 0.75 * d;
    vec3 col = base * light;

    // Baked lightmap: when the mesh carries a real BakedLight atlas, that IS the lighting for this
    // surface, so it replaces the fake directional term (finalColor = diffuse * lightmap). Only kicks
    // in where genuine baked data exists (Map12-style maps); Old-Rift meshes keep the directional look.
    if (uHasLightmap == 1) col = base * texture(uLightmap, vLmUv).rgb;

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
        if (uHasMatCap == 1) {
            float mcGate = (uHasMatCapMask == 1) ? texture(uMatCapMask, uv).r : 1.0;
            col += matcapColour(n) * 0.6 * mcGate;
        }
        if (uHasEmissive == 1) {
            float em = texture(uEmissive, uv).r;
            col += base * em * 1.5;
        }
        col += specTerm * 0.5;   // white-ish highlight, only when uUsesSpec
    }
    // M34 compositing: cutout discards low-alpha texels (opaque depth); transparent keeps the texture alpha
    // for the alpha-blend pass; opaque forces alpha 1. Applies to Basic (0) and RiotApprox (1).
    if (uAlphaMode == 1 && alpha < uAlphaCutoff) discard;
    float outA = (uAlphaMode == 2) ? clamp(alpha, 0.0, 1.0) : 1.0;
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

    public void Initialize(GL gl, bool gles)
    {
        _gl = gl;
        _meshProgram = ShaderUtil.CreateProgram(gl, gles, MeshVert, MeshFrag);
        _mMvp = gl.GetUniformLocation(_meshProgram, "uMvp");
        _mModel = gl.GetUniformLocation(_meshProgram, "uModel");
        _mLight = gl.GetUniformLocation(_meshProgram, "uLight");
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
        _mAlphaCutoff = gl.GetUniformLocation(_meshProgram, "uAlphaCutoff");
        _mClampUv = gl.GetUniformLocation(_meshProgram, "uClampUv");
        _mTwoSided = gl.GetUniformLocation(_meshProgram, "uTwoSided");
        _mMirrored = gl.GetUniformLocation(_meshProgram, "uMirrored");

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
        _ready = true;
    }

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

        var g = new PropGeometry();
        g.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(g.Vao);
        g.Vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, g.Vbo);
        fixed (float* p = inter) _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(inter.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
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
        _submeshes[index].Mirrored = mat.Mirrored;
        _submeshes[index].ClampUv = new Vector2(mat.ClampU ? 1f : 0f, mat.ClampV ? 1f : 0f);
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
            _submeshes[i].Mirrored = false;
            _submeshes[i].ClampUv = Vector2.Zero;
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

    /// <summary>Set (or clear, with pivot=null) the translate gizmo: 3 axis lines from the pivot,
    /// each <paramref name="armLength"/> world units long.</summary>
    public void SetGizmo(Vector3? pivot, float armLength)
    {
        _hasGizmo = pivot.HasValue && armLength > 0f;
        if (pivot.HasValue) { _gizmoPivot = pivot.Value; _gizmoArmLength = armLength; }
    }

    /// <summary>Set the placed-particle markers (M35): a small 3D cross at each world position, plus a larger
    /// highlighted cross at the selected one. An empty list clears them.</summary>
    public void SetParticleMarkers(IReadOnlyList<Vector3> positions, Vector3? selected, float size)
    {
        if (!_ready) return;
        if (positions.Count == 0) _particleVerts = 0;
        else
        {
            var verts = new float[positions.Count * 3 * 2 * 3]; // 3 axes * 2 points * 3 floats
            int k = 0;
            foreach (var p in positions) AppendCross(verts, ref k, p, size);
            UploadLines(_particleVao, _particleVbo, verts, out _particleVerts);
        }
        if (selected is { } sel)
        {
            var v = new float[3 * 2 * 3]; int k = 0; AppendCross(v, ref k, sel, size * 2.2f);
            UploadLines(_particleSelVao, _particleSelVbo, v, out _particleSelVerts);
        }
        else _particleSelVerts = 0;
    }

    /// <summary>Set the animated-prop markers (M38): a small cross at each placed character's position.</summary>
    public void SetPropMarkers(IReadOnlyList<Vector3> positions, float size) => SetCrossMarkers(_propVao, _propVbo, positions, size, out _propVerts);

    /// <summary>Set the cubemap-probe markers (M38).</summary>
    public void SetProbeMarkers(IReadOnlyList<Vector3> positions, float size) => SetCrossMarkers(_probeVao, _probeVbo, positions, size, out _probeVerts);

    private void SetCrossMarkers(uint vao, uint vbo, IReadOnlyList<Vector3> positions, float size, out int vertCount)
    {
        if (!_ready || positions.Count == 0) { vertCount = 0; return; }
        var verts = new float[positions.Count * 3 * 2 * 3];
        int k = 0;
        foreach (var p in positions) AppendCross(verts, ref k, p, size);
        UploadLines(vao, vbo, verts, out vertCount);
    }

    private static void AppendCross(float[] a, ref int k, Vector3 c, float s)
    {
        // X axis
        a[k++] = c.X - s; a[k++] = c.Y; a[k++] = c.Z;  a[k++] = c.X + s; a[k++] = c.Y; a[k++] = c.Z;
        // Y axis
        a[k++] = c.X; a[k++] = c.Y - s; a[k++] = c.Z;  a[k++] = c.X; a[k++] = c.Y + s; a[k++] = c.Z;
        // Z axis
        a[k++] = c.X; a[k++] = c.Y; a[k++] = c.Z - s;  a[k++] = c.X; a[k++] = c.Y; a[k++] = c.Z + s;
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
                _gl.Uniform3(_mLight, -0.4f, -0.85f, -0.45f);
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
                    if (s.Visible && s.AlphaMode != 2) DrawSubmesh(s);

                // Pass 2: transparent (AlphaMode 2) after solids — alpha-blend, depth-test on but NO depth
                // write, so overlapping glass/water composites without occluding itself. (No back-to-front
                // sort — acceptable for a preview.)
                bool anyTransparent = false;
                foreach (var s in _submeshes) if (s.Visible && s.AlphaMode == 2) { anyTransparent = true; break; }
                if (anyTransparent)
                {
                    _gl.Enable(EnableCap.Blend);
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    _gl.DepthMask(false);
                    foreach (var s in _submeshes)
                        if (s.Visible && s.AlphaMode == 2) DrawSubmesh(s);
                    _gl.DepthMask(true);
                }
                _gl.Disable(EnableCap.CullFace); // restore default so the line/gizmo overlays are unaffected
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
            _gl.Uniform2(_mClampUv, 0f, 0f); _gl.Uniform4(_mTint, 1f, 1f, 1f, 1f); _gl.Uniform1(_mMirrored, 0);
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

        // Translate gizmo: 3 axis lines from the selected mesh's pivot (X=red, Y=green, Z=blue), always
        // on top so it stays clickable regardless of what's behind it.
        if (_hasGizmo)
        {
            Span<float> verts = stackalloc float[]
            {
                _gizmoPivot.X, _gizmoPivot.Y, _gizmoPivot.Z, _gizmoPivot.X + _gizmoArmLength, _gizmoPivot.Y, _gizmoPivot.Z,
                _gizmoPivot.X, _gizmoPivot.Y, _gizmoPivot.Z, _gizmoPivot.X, _gizmoPivot.Y + _gizmoArmLength, _gizmoPivot.Z,
                _gizmoPivot.X, _gizmoPivot.Y, _gizmoPivot.Z, _gizmoPivot.X, _gizmoPivot.Y, _gizmoPivot.Z + _gizmoArmLength,
            };
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _gizmoVbo);
            unsafe
            {
                fixed (float* p = verts)
                    _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
            }
            _gl.BindVertexArray(_gizmoVao);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

            _gl.UseProgram(_lineProgram);
            _gl.UniformMatrix4(_lMvp, 1, false, in m.M11);
            _gl.Disable(EnableCap.DepthTest);
            _gl.LineWidth(2.5f);
            _gl.Uniform4(_lColor, 0.95f, 0.25f, 0.25f, 1f); // X red
            _gl.DrawArrays(PrimitiveType.Lines, 0, 2);
            _gl.Uniform4(_lColor, 0.3f, 0.9f, 0.35f, 1f);   // Y green
            _gl.DrawArrays(PrimitiveType.Lines, 2, 2);
            _gl.Uniform4(_lColor, 0.3f, 0.55f, 0.98f, 1f);  // Z blue
            _gl.DrawArrays(PrimitiveType.Lines, 4, 2);
            _gl.LineWidth(1f);
            _gl.BindVertexArray(0);
        }

        // Placed-object markers, always on top: particles cyan (M35), animated props orange + cubemap probes
        // green (M38); the selected one larger + bright yellow.
        if (_particleVerts > 0 || _particleSelVerts > 0 || _propVerts > 0 || _probeVerts > 0)
        {
            _gl.UseProgram(_lineProgram);
            _gl.UniformMatrix4(_lMvp, 1, false, in m.M11);
            _gl.Disable(EnableCap.DepthTest);
            DrawMarkerSet(_particleVao, _particleVerts, 0.30f, 0.85f, 0.95f); // particles cyan
            DrawMarkerSet(_propVao, _propVerts, 1.0f, 0.55f, 0.15f);          // animated props orange
            DrawMarkerSet(_probeVao, _probeVerts, 0.30f, 0.95f, 0.45f);       // cubemap probes green
            if (_particleSelVerts > 0)
            {
                _gl.LineWidth(2.5f);
                _gl.Uniform4(_lColor, 1.0f, 0.9f, 0.2f, 1f); // selected placeable bright yellow
                _gl.BindVertexArray(_particleSelVao);
                _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_particleSelVerts);
                _gl.LineWidth(1f);
            }
            _gl.BindVertexArray(0);
        }
    }

    private void DrawMarkerSet(uint vao, int vertCount, float r, float g, float b)
    {
        if (vertCount == 0) return;
        _gl.Uniform4(_lColor, r, g, b, 1f);
        _gl.BindVertexArray(vao);
        _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)vertCount);
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
