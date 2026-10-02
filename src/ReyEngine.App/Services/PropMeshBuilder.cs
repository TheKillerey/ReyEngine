using ReyEngine.App.ViewModels;
using ReyEngine.Core.Decoding;
using ReyEngine.Formats.Animation;
using ReyEngine.Formats.Materials;
using ReyEngine.Formats.Meshes;
using ReyEngine.Formats.Shaders;
using ReyEngine.Formats.Skeletons;

namespace ReyEngine.App.Services;

/// <summary>
/// M807: how much of a prop the caller wants decoded.
/// <list type="bullet">
/// <item><see cref="Full"/> - what the editor's viewport has always had built: the mesh, the skin's own scale, the GL
/// viewport's per-submesh texture layers and render state, and the skeleton with its idle clip.</item>
/// <item><see cref="Dx11Only"/> - the mesh, the skin's scale and the two byte arrays Riot's shaders are prepared from
/// (<see cref="PropMesh.SknBytes"/>, <see cref="PropMesh.SkinBinBytes"/>). No texture is decoded for the CPU (the D3D11
/// path decodes its own, from the skin bin, in <see cref="Dx11CharacterScene"/>) and no skeleton or clip is read, so a
/// prop stands in its bind pose - which is also what the viewport shows until Anim is switched on.</item>
/// </list>
/// </summary>
public enum PropMeshDetail { Full, Dx11Only }

/// <summary>M807: how a <see cref="PropMeshBuilder"/> reaches the game's data. Delegates rather than a view model so the
/// same builder runs under the editor (its mounts, its texture decode, its clip tables) and under the Content Browser's
/// map thumbnails (their own reader, on their own thread).</summary>
/// <param name="ReadByPath">An asset by the path a bin names it with (<c>null</c> when it is not there).</param>
/// <param name="LoadTexture">A decoded texture by path; only asked for under <see cref="PropMeshDetail.Full"/>.</param>
/// <param name="FindClip">The named clip of a skin's animation graph, or null; only asked for under
/// <see cref="PropMeshDetail.Full"/>.</param>
/// <param name="FindIdleClip">The skin's idle clip, or null; only asked for under <see cref="PropMeshDetail.Full"/>.</param>
public sealed record PropMeshSources(
    Func<string, byte[]?> ReadByPath,
    Func<uint, string?> ResolveBinName,
    Func<ulong, string?> ResolveWadPath,
    Func<string, TextureImage?> LoadTexture,
    Func<string, string, AnimationClip?> FindClip,
    Func<string, AnimationClip?> FindIdleClip);

/// <summary>
/// <para>M807: the prop decode the editor's viewport has run since M41, lifted out of <c>MainWindowViewModel</c> so the
/// Content Browser's map thumbnails build their props with the SAME code - "the same builder or nothing", as
/// <see cref="Dx11SceneBuilder"/> is for the map. Nothing about what the viewport draws changes: the view model's
/// <c>TryBuildPropMesh</c> is now a call into this with <see cref="PropMeshDetail.Full"/>, and every line below is the
/// body it had.</para>
///
/// <para>A prop is a character skin: <c>data/&lt;skin&gt;.bin</c> names the .skn, the .skn is decoded, and the bin's
/// <c>skinScale</c> is the game's own scale for the model (M676). M805 added the optional <c>hostBin</c> for a banner
/// whose materials live in the map's shipping bin.</para>
/// </summary>
public sealed class PropMeshBuilder
{
    private readonly PropMeshSources _src;

    public PropMeshBuilder(PropMeshSources sources) => _src = sources;

    /// <param name="hostBin">M805: the bin the game has loaded beside this skin, for materials no dependency of the skin
    /// bin holds (an esports banner's live in the map's shipping bin). Null for every other prop: unchanged.</param>
    public PropMesh? TryBuild(string skin, Dictionary<string, TextureImage?> texCache, string? clip,
        LoadedBin? hostBin, PropMeshDetail detail)
    {
        try
        {
            var binBytes = _src.ReadByPath("data/" + skin.ToLowerInvariant() + ".bin");
            if (binBytes is null) return null;
            var meshRef = SkinMeshExtractor.Extract(binBytes, _src.ResolveWadPath);
            if (meshRef?.SimpleSkin is not { } sknPath) return null;
            var sknBytes = _src.ReadByPath(sknPath);
            if (sknBytes is null) return null;

            var mesh = SkinnedMeshDecoder.Decode(sknBytes);

            // M676: the skin's own scale, which the game applies to the whole model and this preview never
            // did - Baron and the camps drew at the mesh's authored size whatever the bin said.
            float skinScale = 1f;
            try
            {
                var doc = MaterialDocument.Parse(binBytes, _src.ResolveBinName, _src.ResolveWadPath, _src.ReadByPath);
                if (doc.SkinMesh?.SkinScale is { } authored && authored > 0f) skinScale = authored;
            }
            catch { /* an unparseable skin keeps scale 1 rather than losing the prop */ }

            if (detail == PropMeshDetail.Dx11Only)
            {
                // M807: nothing the D3D11 draw reads is decoded here twice. Riot's shaders get the skin's textures from
                // Dx11CharacterScene.Prepare (SknBytes + SkinBinBytes + HostBin); the submeshes carry the ranges the
                // diffuse-only fallback would draw, without a texture.
                var ranges = mesh.SubMeshes.Select(s => new PropSubmesh(s.StartIndex, s.IndexCount, null)).ToList();
                return new PropMesh(skin, mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, ranges)
                {
                    SknMesh = mesh,
                    SknBytes = sknBytes, SkinBinBytes = binBytes, SkinScale = skinScale,
                    HostBin = hostBin,
                };
            }

            var mat = ChampionMaterialResolver.Resolve(binBytes, _src.ResolveBinName, _src.ResolveWadPath, _src.ReadByPath, hostBin);   // M777, M805
            TextureImage? Tex(string? path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                if (texCache.TryGetValue(path, out var img)) return img;
                return texCache[path] = _src.LoadTexture(path);
            }
            // M678: the same layers and render state the character window resolves for this skin (M664),
            // so a prop in the GL viewport blends, cuts out, tints and glows as the window shows it.
            var subs = mesh.SubMeshes
                .Select(s => new PropSubmesh(s.StartIndex, s.IndexCount, Tex(mat.For(s.Material) ?? meshRef.DefaultTexture))
                {
                    Mask = Tex(mat.ForMask(s.Material)),
                    Gradient = Tex(mat.ForGradient(s.Material)),
                    Emissive = Tex(mat.ForEmissive(s.Material)),
                    MatCap = Tex(mat.ForMatCap(s.Material)),
                    MatCapMask = Tex(mat.ForMatCapMask(s.Material)),
                    Material = mat.HasAny ? MapSubmeshResources.ToSubmeshMaterial(mat.Profile(s.Material)) with { BlendWritesDepth = true } : null,
                })
                .ToList();

            // M54: idle-animation payload — the character's skeleton + a best-match idle .anm, so the
            // viewport can play the ambient idles (Baron breathing, camps shuffling...).
            SkeletonAsset? skeleton = null;
            AnimationClip? idle = null;
            if (mesh.CanSkin && meshRef.Skeleton is { } sklPath)
            {
                try
                {
                    var sklBytes = _src.ReadByPath(sklPath);
                    if (sklBytes is not null) skeleton = SkeletonDecoder.Decode(sklBytes);
                    // M677: the chosen clip when there is one, the idle otherwise - and the idle when the
                    // chosen name resolves to nothing, rather than a prop frozen in bind pose.
                    if (skeleton is not null) idle = (clip is not null ? TryFindClip(skin, clip) : null) ?? TryFindIdleClip(skin);
                }
                catch { skeleton = null; idle = null; }
            }
            return new PropMesh(skin, mesh.Positions, mesh.Normals, mesh.Uvs, mesh.Indices, subs)
            {
                SknMesh = mesh, Skeleton = skeleton, IdleClip = idle,
                SknBytes = sknBytes, SkinBinBytes = binBytes, SkinScale = skinScale,   // M676
                HostBin = hostBin,   // M805: the D3D11 scene resolves the same materials
            };
        }
        catch { return null; }
    }

    private AnimationClip? TryFindClip(string skin, string name) => _src.FindClip(skin, name);
    private AnimationClip? TryFindIdleClip(string skin) => _src.FindIdleClip(skin);

    /// <summary>M676: the D3D11 scene for a placed prop - Riot's shaders, the skin's textures, parameters and drivers at
    /// rest - prepared exactly as a champion is for the character window. Null when the mesh carries no bytes (an added
    /// mesh); the D3D11 prop driver then keeps its diffuse-only draw for that mesh. M807: static, so the editor's view
    /// model and the map thumbnails (their own cache, index and reader) prepare a prop identically.</summary>
    public static PreparedCharacterScene? PrepareDx11Scene(PropMesh mesh, ShaderCacheReader cache,
        ShaderPermutationIndex? perms, Func<ulong, byte[]?> readAsset, Func<uint, string?> resolveBinName,
        Func<ulong, string?> resolveWadPath)
    {
        if (mesh.SknBytes is null) return null;
        try
        {
            return Dx11CharacterScene.Prepare(mesh.SknBytes, mesh.SkinBinBytes, cache, perms,
                readAsset: readAsset,
                resolveBinName: resolveBinName,
                resolveWadPath: resolveWadPath,
                fallbackShader: Dx11CharacterScene.DefaultCharacterShader,
                // M805: an esports banner's materials live in the map's shipping bin, parsed once when the banners were built
                hostBin: mesh.HostBin,
                // M732: this mesh was already decoded on the thread pool when the prop set was built.
                decodedMesh: mesh.SknMesh);
        }
        catch { return null; }
    }
}
