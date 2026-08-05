using System.Numerics;
using System.Security.Cryptography;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Memory;
using ReyEngine.Core.Decoding;
using ReyEngine.Core.Hashing;

namespace ReyEngine.Formats.MapGeo;

public enum LegacyMaterialRole { Normal, Decal, Grass, FourBlendTerrain }

public sealed record LegacyPortShaderOptions(
    string NormalShader,
    string DecalShader,
    string GrassShader,
    string TerrainShader)
{
    public static LegacyPortShaderOptions Defaults { get; } = new(
        LegacyMapPorter.NormalShader,
        LegacyMapPorter.DecalShader,
        LegacyMapPorter.GrassShader,
        LegacyMapPorter.TerrainShader);
}

public sealed record LegacyTextureCopy(string SourcePath, string TargetPath, byte[] Bytes);

public sealed record LegacyMaterialPlan(
    string Name,
    LegacyMaterialRole Role,
    string Shader,
    IReadOnlyDictionary<string, string> Samplers,
    IReadOnlyDictionary<string, Vector4> Parameters,
    IReadOnlyDictionary<string, bool> Switches,
    IReadOnlyDictionary<string, bool> Macros,
    bool BlendEnabled = false,
    int? SourceBlendFactor = null,
    int? DestinationBlendFactor = null);

public sealed record LegacyMapPortResult(
    byte[] MapGeoBytes,
    IReadOnlyList<LegacyTextureCopy> Textures,
    IReadOnlyList<LegacyMaterialPlan> Materials,
    string SourceFile,
    string SourceFormat,
    int SourceMeshCount,
    int ImportedMeshCount,
    int RemovedBaseMeshCount,
    int PreservedRenderRegionMeshCount,
    int SourceMaterialCount,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Converts Riot's pre-mapgeo NVR/WGEO environments into a modern mapgeo container. The destination
/// remains authoritative: its v18 render-region meshes and non-geometry tail are retained. Imported
/// geometry is grouped by effective texture set and split only at the u16 vertex limit, which collapses
/// thousands of old NVR draw objects without duplicating materials.
/// </summary>
public static class LegacyMapPorter
{
    public const string NormalShader = "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest";
    public const string DecalShader = "Shaders/StaticMesh/DefaultEnv_Flat_AlphaTest";
    public const string GrassShader = "Shaders/StaticMesh/VertexDeform";
    public const string TerrainShader = "Shaders/StaticMesh/4TextureBlend_WorldProjected";
    private const int MaxVertices = 65535;

    public static LegacyMapPortResult ApplyShaderOptions(LegacyMapPortResult result, LegacyPortShaderOptions options)
    {
        string ShaderFor(LegacyMaterialRole role) => role switch
        {
            LegacyMaterialRole.Decal => options.DecalShader,
            LegacyMaterialRole.Grass => options.GrassShader,
            LegacyMaterialRole.FourBlendTerrain => options.TerrainShader,
            _ => options.NormalShader,
        };
        return result with
        {
            Materials = result.Materials.Select(material => material with { Shader = ShaderFor(material.Role) }).ToList(),
        };
    }

    public static LegacyMapPortResult Port(string sourceRoot, byte[] destinationMapGeo,
        string? destinationMapGeoPath = null)
    {
        string source = FindSingleSource(sourceRoot);
        byte[] sourceBytes = File.ReadAllBytes(source);
        bool isWgeo = sourceBytes.AsSpan().StartsWith("WGEO"u8);
        bool isNvr = sourceBytes.Length >= 3 && sourceBytes[0] == 'N' && sourceBytes[1] == 'V' && sourceBytes[2] == 'R';
        if (!isWgeo && !isNvr) throw new InvalidDataException("The selected room file is neither WGEO nor NVR.");

        if (!MapGeoBinary.TryReadEditable(destinationMapGeo, out var target))
            throw new InvalidDataException("The destination mapgeo is not byte-exact editable; the legacy port was not applied.");
        if (target.Version < 17)
            throw new InvalidDataException("The legacy porter currently requires a mapgeo v17 or v18 destination.");

        var textureIndex = new LegacyTextureIndex(Path.GetDirectoryName(source)!);
        var nvrMaterials = isNvr ? ParseNvrMaterials(sourceBytes) : new(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        using var stream = new MemoryStream(sourceBytes, writable: false);
        using var environment = isWgeo ? WorldGeometry.Load(stream) : SimpleEnvironment.Load(stream);

        string slug = Slug(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(source))!) ?? "legacy");
        var textureCopies = new Dictionary<string, LegacyTextureCopy>(StringComparer.OrdinalIgnoreCase);
        var textureTargetsByContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var decodedTextures = new Dictionary<string, TextureImage?>(StringComparer.OrdinalIgnoreCase);

        string? PortTexture(string? reference, bool required = true)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            if (!textureIndex.TryResolve(reference, out string file))
            {
                if (required) warnings.Add($"Texture '{reference}' was not found under {Path.GetDirectoryName(source)}.");
                return null;
            }
            byte[] bytes = File.ReadAllBytes(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".dds")
            {
                // Modern map materials should not keep legacy container types. DXT1/DXT5 blocks can be
                // moved losslessly into TEX after reversing their mip order; unusual DDS formats use
                // the RGBA/BC3 fallback. Existing TEX inputs remain byte-exact.
                if (!TexWriter.TryWrapDds(bytes, out var converted))
                    converted = TexWriter.Write(TextureDecoder.Decode(bytes), TexFormat.Bc3, mipmaps: true);
                bytes = converted;
                ext = ".tex";
            }
            else if (ext == ".tga")
            {
                bytes = TexWriter.Write(TextureDecoder.Decode(bytes), TexFormat.Bc3, mipmaps: true);
                ext = ".tex";
            }
            string digest = Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 6)).ToLowerInvariant();
            if (!textureTargetsByContent.TryGetValue(digest, out string? targetPath))
            {
                string stem = Slug(Path.GetFileNameWithoutExtension(file));
                targetPath = $"assets/maps/legacyimport/{slug}/textures/{stem}_{digest}{ext}";
                textureTargetsByContent[digest] = targetPath;
                textureCopies[targetPath] = new LegacyTextureCopy(file, targetPath, bytes);
            }
            return targetPath;
        }

        TextureImage? DecodeTexture(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference) || !textureIndex.TryResolve(reference, out string file)) return null;
            if (decodedTextures.TryGetValue(file, out var cached)) return cached;
            try { return decodedTextures[file] = TextureDecoder.Decode(File.ReadAllBytes(file)); }
            catch (Exception ex) { warnings.Add($"Could not inspect texture '{reference}': {ex.Message}"); return decodedTextures[file] = null; }
        }

        var accumulators = new Dictionary<SurfaceKey, List<MeshAccumulator>>();
        int sourceMaterialCount = 0;
        int rejectedTriangles = 0;
        var sourceMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terrainBlendReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int meshIndex = 0;
        foreach (var mesh in environment.Meshes)
        {
            try
            {
                var view = mesh.VerticesView;
                int vertexCount = view.VertexCount;
                if (vertexCount == 0 || mesh.Indices.Count < 3) { meshIndex++; continue; }

                var pos = ReadVector3(view.GetAccessor(ElementName.Position), vertexCount);
                var normals = view.TryGetAccessor(ElementName.Normal, out var nAcc) ? ReadVector3(nAcc, vertexCount) : null;
                var uv0 = view.TryGetAccessor(ElementName.Texcoord0, out var uvAcc) ? ReadVector2(uvAcc, vertexCount) : null;
                var uv7 = view.TryGetAccessor(ElementName.Texcoord7, out var uv7Acc) ? ReadVector2(uv7Acc, vertexCount) : null;
                var transform = mesh.Transform;
                var normalMatrix = Matrix4x4.Invert(transform, out var inverse)
                    ? Matrix4x4.Transpose(inverse) : transform;
                var transformedPositions = pos.Select(p => Vector3.Transform(p, transform)).Where(Reasonable).ToArray();
                Vector3 meshPivot = transformedPositions.Length == 0 ? transform.Translation : new(
                    (transformedPositions.Min(p => p.X) + transformedPositions.Max(p => p.X)) * 0.5f,
                    transformedPositions.Min(p => p.Y),
                    (transformedPositions.Min(p => p.Z) + transformedPositions.Max(p => p.Z)) * 0.5f);

                var submeshes = mesh.Submeshes.Count > 0
                    ? mesh.Submeshes.Select(s => (s.Material ?? "", s.StartIndex, s.IndexCount)).ToList()
                    : new List<(string, int, int)> { ("", 0, mesh.Indices.Count) };

                foreach (var (materialName, startIndex, indexCount) in submeshes)
                {
                    sourceMaterials.Add(materialName);
                    var raw = nvrMaterials.GetValueOrDefault(StripNvrPrefix(materialName));
                    string? baseRef = raw?.Base;
                    if (string.IsNullOrWhiteSpace(baseRef)) baseRef = mesh.StationaryLight.Texture;
                    string? baseTarget = PortTexture(baseRef);
                    if (baseTarget is null) continue;

                    bool fourBlend = raw is { Blend.Length: > 0, Color1.Length: > 0, Color2.Length: > 0, Color3.Length: > 0 }
                                     && uv7 is not null && DecodeTexture(raw.Blend) is not null;
                    bool cutout = !fourBlend && HasCutoutAlpha(DecodeTexture(baseRef));
                    LegacyMaterialRole role = fourBlend ? LegacyMaterialRole.FourBlendTerrain
                        : cutout && LooksLikeGrass(materialName, baseRef) ? LegacyMaterialRole.Grass
                        : cutout && LooksLikeDecal(materialName, baseRef) ? LegacyMaterialRole.Decal
                        : LegacyMaterialRole.Normal;

                    var samplers = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    TextureImage? blendMask = null;
                    if (fourBlend)
                    {
                        terrainBlendReferences.Add(raw!.Blend);
                        blendMask = DecodeTexture(raw.Blend);
                        string? middle = PortTexture(raw.Color1);
                        string? top = PortTexture(raw.Color2);
                        string? extras = PortTexture(raw.Color3);
                        if (middle is null || top is null || extras is null) { warnings.Add($"Ground material '{materialName}' was missing a layer; imported as a normal alpha-tested surface."); role = LegacyMaterialRole.Normal; samplers["DiffuseTexture"] = baseTarget; }
                        else
                        {
                            samplers["Bottom_Texture"] = baseTarget;
                            samplers["Middle_Texture"] = middle;
                            samplers["Top_Texture"] = top;
                            samplers["Extras_Texture"] = extras;
                        }
                    }
                    else samplers["DiffuseTexture"] = baseTarget;

                    var key = new SurfaceKey(role, string.Join("|", samplers.Select(kv => kv.Key + "=" + kv.Value)),
                        mesh.DisableBackfaceCulling || role == LegacyMaterialRole.Grass);
                    if (!accumulators.TryGetValue(key, out var chunks)) accumulators[key] = chunks = new();
                    if (chunks.Count == 0) chunks.Add(new MeshAccumulator(key, samplers));

                    int end = Math.Min(mesh.Indices.Count, startIndex + indexCount);
                    for (int i = startIndex; i + 2 < end; i += 3)
                    {
                        uint u0 = mesh.Indices[i], u1 = mesh.Indices[i + 1], u2 = mesh.Indices[i + 2];
                        if (u0 >= vertexCount || u1 >= vertexCount || u2 >= vertexCount) continue;
                        int i0 = (int)u0, i1 = (int)u1, i2 = (int)u2;
                        Vector3 p0 = Vector3.Transform(pos[i0], transform);
                        Vector3 p1 = Vector3.Transform(pos[i1], transform);
                        Vector3 p2 = Vector3.Transform(pos[i2], transform);
                        if (!Reasonable(p0) || !Reasonable(p1) || !Reasonable(p2)) { rejectedTriangles++; continue; }
                        var chunk = chunks[^1];
                        int needed = chunk.NewVertexCount(meshIndex, i0, i1, i2);
                        if (chunk.VertexCount + needed > MaxVertices)
                        { chunk = new MeshAccumulator(key, samplers); chunks.Add(chunk); }

                        LegacyVertex Make(int index)
                        {
                            Vector3 p = Vector3.Transform(pos[index], transform);
                            Vector3 n = normals is null ? Vector3.Zero : Vector3.TransformNormal(normals[index], normalMatrix);
                            if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n); else n = Vector3.UnitY;
                            Vector2 uv = uv0?[index] ?? Vector2.Zero;
                            Vector4 color = role switch
                            {
                                LegacyMaterialRole.FourBlendTerrain when blendMask is not null => Sample(blendMask, uv7![index]),
                                _ => Vector4.One,
                            };
                            return new LegacyVertex(p, n, uv, color, meshPivot, normals is not null);
                        }
                        chunk.AddTriangle(meshIndex, i0, i1, i2, Make);
                    }
                }
            }
            catch (Exception ex) { warnings.Add($"Legacy mesh {meshIndex}: {ex.Message}"); }
            meshIndex++;
        }
        if (rejectedTriangles > 0)
            warnings.Add($"Skipped {rejectedTriangles:n0} triangle(s) containing invalid legacy sentinel coordinates.");
        sourceMaterialCount = sourceMaterials.Count;

        // The modern world-projected terrain shader receives its RGB paint canvas from the engine rather
        // than from samplerValues. Legacy NVR stores that same map-wide canvas as channel 1. Publish it at
        // the path derived from the destination mapgeo so both the game and both editor viewports bind it.
        if (terrainBlendReferences.Count > 0 && !string.IsNullOrWhiteSpace(destinationMapGeoPath))
        {
            string selected = terrainBlendReferences.First();
            if (textureIndex.TryResolve(selected, out string blendFile))
            {
                var blendImage = TextureDecoder.Decode(File.ReadAllBytes(blendFile));
                string targetPath = MapGeoMaterialResolver.TerrainBlendTexturePathFor(destinationMapGeoPath);
                byte[] blendTex = TexWriter.Write(blendImage, TexFormat.Bc3, mipmaps: true);
                textureCopies[targetPath] = new LegacyTextureCopy(blendFile, targetPath, blendTex);
            }
            else warnings.Add($"Terrain blend texture '{selected}' was not found; the ground uses its bottom layer only.");
            if (terrainBlendReferences.Count > 1)
                warnings.Add($"The NVR references {terrainBlendReferences.Count:n0} terrain blend canvases; " +
                    $"'{selected}' was selected for the destination map-wide paint resource.");
        }

        var built = accumulators.Values.SelectMany(x => x).Where(x => x.IndexCount > 0).ToList();
        if (built.Count == 0) throw new InvalidDataException("The legacy environment contained no importable textured triangles.");

        var preserved = target.Meshes.Where(m => m.HasRegionHash && m.RegionHash != 0).ToList();
        int preservedCount = preserved.Count;
        int removed = target.Meshes.Count - preserved.Count;
        target.Meshes = preserved;
        target.Compact();

        var materialNames = BuildMaterialNames(slug, built.Select(x => new MaterialKey(x.Key.Role, x.Key.TextureSet)));
        foreach (var acc in built)
            AddMesh(target, acc, materialNames[new MaterialKey(acc.Key.Role, acc.Key.TextureSet)]);

        byte[] ported = target.Write();
        var decoded = MapGeoDecoder.Decode(ported);
        // Old NVR ground uses much larger triangles than modern mapgeo. A 1k culling grid avoids
        // duplicating those triangles into hundreds of 500-unit cells while retaining useful culling.
        ported = MapGeoWriter.WriteWithRegeneratedBucketGrids(ported, decoded, targetBucketSize: 1000f);
        var verified = MapGeoDecoder.Decode(ported);
        int preservedAfter = verified.Meshes.Count(m => m.RegionHash != 0);
        if (preservedAfter != preservedCount)
            throw new InvalidDataException($"Render-region verification failed: retained {preservedAfter} of {preservedCount} meshes.");

        var materialPlans = BuildMaterialPlans(materialNames, built, LegacyPortShaderOptions.Defaults);
        return new LegacyMapPortResult(ported, textureCopies.Values.ToList(), materialPlans, source,
            isWgeo ? "WGEO" : "NVR", environment.Meshes.Count, built.Count, removed, preservedCount,
            sourceMaterialCount, warnings.Distinct().ToList());
    }

    private static string FindSingleSource(string root)
    {
        if (File.Exists(root) && (Path.GetFileName(root).Equals("room.nvr", StringComparison.OrdinalIgnoreCase)
                                 || Path.GetFileName(root).Equals("room.wgeo", StringComparison.OrdinalIgnoreCase))) return root;
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = Directory.EnumerateFiles(root, "room.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".nvr", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".wgeo", StringComparison.OrdinalIgnoreCase))
            .Take(3).ToList();
        return files.Count switch
        {
            1 => files[0],
            0 => throw new FileNotFoundException("No Scene/room.nvr or Scene/room.wgeo was found in the selected folder."),
            _ => throw new InvalidOperationException("The selected folder contains multiple legacy maps. Select the specific LEVELS/MapN folder."),
        };
    }

    private static Dictionary<MaterialKey, string> BuildMaterialNames(string slug, IEnumerable<MaterialKey> keys) =>
        keys.Distinct().ToDictionary(k => k, k =>
        {
            string source = k.Role + "|" + k.TextureSet;
            string digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)).AsSpan(0, 6))
                .ToLowerInvariant();
            return $"LegacyPort/{slug}/{k.Role}_{digest}";
        });

    private static IReadOnlyList<LegacyMaterialPlan> BuildMaterialPlans(
        IReadOnlyDictionary<MaterialKey, string> names, IReadOnlyList<MeshAccumulator> meshes,
        LegacyPortShaderOptions options)
    {
        var result = new List<LegacyMaterialPlan>();
        foreach (var (key, name) in names)
        {
            var sample = meshes.First(m => m.Key.Role == key.Role && m.Key.TextureSet == key.TextureSet).Samplers;
            string shader = key.Role switch
            {
                LegacyMaterialRole.Decal => options.DecalShader,
                LegacyMaterialRole.Grass => options.GrassShader,
                LegacyMaterialRole.FourBlendTerrain => options.TerrainShader,
                _ => options.NormalShader,
            };
            IReadOnlyDictionary<string, string> samplerPlan = key.Role == LegacyMaterialRole.FourBlendTerrain
                ? new Dictionary<string, string>(sample)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["__diffuse__"] = sample.Values.First() };
            var parameters = key.Role == LegacyMaterialRole.FourBlendTerrain
                ? new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase) { ["WS_Multiplier"] = new(0.01f, 0, 0, 0) }
                : key.Role == LegacyMaterialRole.Decal
                    ? new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase) { ["AlphaTestValue"] = new(0.005f, 0, 0, 0) }
                    : new Dictionary<string, Vector4>();
            var switches = key.Role == LegacyMaterialRole.FourBlendTerrain
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["USE_TOP"] = true, ["USE_EXTRAS"] = true }
                : new Dictionary<string, bool>();
            bool decal = key.Role == LegacyMaterialRole.Decal;
            result.Add(new LegacyMaterialPlan(name, key.Role, shader, samplerPlan, parameters, switches,
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["NO_BAKED_LIGHTING"] = true },
                BlendEnabled: decal, SourceBlendFactor: decal ? 6 : null, DestinationBlendFactor: decal ? 7 : null));
        }
        return result;
    }

    private static void AddMesh(MapGeoBinary target, MeshAccumulator source, string material)
    {
        source.FinishNormals();
        bool hasColor = source.Key.Role == LegacyMaterialRole.FourBlendTerrain;
        bool hasGrassPivot = source.Key.Role == LegacyMaterialRole.Grass;
        var decl = new MapGeoBinary.VertexDeclaration { Usage = 0 };
        decl.Elements.Add((MapGeoBinary.ElemPosition, MapGeoBinary.FmtXYZ_Float32));
        decl.Elements.Add((MapGeoBinary.ElemNormal, MapGeoBinary.FmtXYZ_Float32));
        if (hasColor) decl.Elements.Add((MapGeoBinary.ElemPrimaryColor, MapGeoBinary.FmtBGRA_Packed8888));
        if (hasGrassPivot) decl.Elements.Add((MapGeoBinary.ElemTexcoord5, MapGeoBinary.FmtXYZ_Float32));
        decl.Elements.Add((MapGeoBinary.ElemTexcoord0, MapGeoBinary.FmtXY_Float32));
        decl.Padding = new byte[8 * (15 - decl.Elements.Count)];
        int declId = target.Declarations.Count; target.Declarations.Add(decl);

        using var vertices = new MemoryStream(source.VertexCount * decl.Stride);
        using (var writer = new BinaryWriter(vertices, System.Text.Encoding.UTF8, leaveOpen: true))
            foreach (var v in source.Vertices)
            {
                writer.Write(v.Position.X); writer.Write(v.Position.Y); writer.Write(v.Position.Z);
                writer.Write(v.Normal.X); writer.Write(v.Normal.Y); writer.Write(v.Normal.Z);
                if (hasColor)
                {
                    writer.Write((byte)Math.Clamp((int)MathF.Round(v.Color.Z * 255), 0, 255));
                    writer.Write((byte)Math.Clamp((int)MathF.Round(v.Color.Y * 255), 0, 255));
                    writer.Write((byte)Math.Clamp((int)MathF.Round(v.Color.X * 255), 0, 255));
                    writer.Write((byte)Math.Clamp((int)MathF.Round(v.Color.W * 255), 0, 255));
                }
                if (hasGrassPivot)
                {
                    writer.Write(v.Pivot.X); writer.Write(v.Pivot.Y); writer.Write(v.Pivot.Z);
                }
                writer.Write(v.Uv.X); writer.Write(v.Uv.Y);
            }
        int vb = target.VertexBuffers.Count;
        target.VertexBuffers.Add(new MapGeoBinary.VertexBuffer { HasVisibility = true, Visibility = 0xff, Data = vertices.ToArray() });

        byte[] indexBytes = new byte[source.Indices.Count * 2];
        for (int i = 0; i < source.Indices.Count; i++) BitConverter.TryWriteBytes(indexBytes.AsSpan(i * 2, 2), source.Indices[i]);
        int ib = target.IndexBuffers.Count;
        target.IndexBuffers.Add(new MapGeoBinary.IndexBuffer { HasVisibility = true, Visibility = 0xff, Data = indexBytes });

        var mesh = new MapGeoBinary.Mesh
        {
            VertexCount = source.VertexCount, VertexDeclarationBase = declId, IndexCount = source.Indices.Count,
            IndexBufferId = ib, HasVisibility = true, Visibility = 0xff,
            HasRegionHash = target.Version >= 18, RegionHash = 0,
            HasVcHash = target.Version >= 15, VisibilityControllerPathHash = 0,
            HasDisableBackface = true, DisableBackfaceCulling = source.Key.DoubleSided,
            BoundsMin = source.BoundsMin, BoundsMax = source.BoundsMax, Transform = Matrix4x4.Identity,
            QualityFilter = 31, HasLayerTransition = target.Version >= 14, LayerTransition = 0,
            RenderFlags = 0, RenderFlagsIsUshort = target.Version >= 16,
            BakedLight = new MapGeoBinary.Channel { Scale = Vector2.One },
            StationaryLight = new MapGeoBinary.Channel { Scale = Vector2.One },
            BakedPaintScale = Vector2.One, BakedPaintBias = Vector2.Zero,
        };
        mesh.VertexBufferIds.Add(vb);
        mesh.Submeshes.Add(new MapGeoBinary.Submesh
        {
            Hash = HashAlgorithms.Fnv1a(material), Material = material, StartIndex = 0,
            IndexCount = source.Indices.Count, MinVertex = 0, MaxVertex = source.VertexCount - 1,
        });
        target.Meshes.Add(mesh);
    }

    private sealed record NvrMaterial(string Base, string Blend, string Color1, string Color2, string Color3);

    private static Dictionary<string, NvrMaterial> ParseNvrMaterials(byte[] data)
    {
        var result = new Dictionary<string, NvrMaterial>(StringComparer.OrdinalIgnoreCase);
        if (data.Length < 28) return result;
        ushort major = BitConverter.ToUInt16(data, 4);
        int count = BitConverter.ToInt32(data, 8);
        if (major is < 8 or > 9 || count is <= 0 or > 100000) return result;
        const int start = 28, stride = 2988, nameLength = 260, textureOffset = 284, channelStride = 340;
        for (int i = 0; i < count; i++)
        {
            int record = start + i * stride;
            if (record + stride > data.Length) break;
            string name = ReadCString(data, record, nameLength);
            string Channel(int c) => ReadCString(data, record + textureOffset + c * channelStride, 256);
            if (name.Length > 0) result[name] = new(Channel(0), Channel(1), Channel(2), Channel(4), Channel(6));
        }
        return result;
    }

    private static string ReadCString(byte[] data, int offset, int max)
    {
        int end = offset, limit = Math.Min(data.Length, offset + max);
        while (end < limit && data[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(data, offset, end - offset);
    }

    private static string StripNvrPrefix(string value) => value.StartsWith("NVRMaterial_", StringComparison.OrdinalIgnoreCase) ? value[12..] : value;

    private static bool HasCutoutAlpha(TextureImage? image)
    {
        if (image is null) return false;
        int transparent = 0, opaque = 0, pixels = image.Width * image.Height;
        int step = Math.Max(1, pixels / 65536);
        for (int p = 0; p < pixels; p += step)
        {
            byte a = image.Rgba[p * 4 + 3];
            if (a < 245) transparent++; else opaque++;
        }
        int sampled = transparent + opaque;
        return transparent > sampled / 200 && opaque > sampled / 200;
    }

    private static bool LooksLikeGrass(string material, string? texture)
    {
        string value = (material + " " + texture).ToLowerInvariant();
        return value.Contains("grass", StringComparison.Ordinal)
            || value.Contains("tuft", StringComparison.Ordinal)
            || value.Contains("plant", StringComparison.Ordinal)
            || value.Contains("fern", StringComparison.Ordinal)
            || value.Contains("brush", StringComparison.Ordinal)
            || value.Contains("bush", StringComparison.Ordinal)
            || value.Contains("shrub", StringComparison.Ordinal)
            || value.Contains("weed", StringComparison.Ordinal)
            || value.Contains("reed", StringComparison.Ordinal);
    }

    private static bool LooksLikeDecal(string material, string? texture)
    {
        string value = (material + " " + texture).ToLowerInvariant();
        return value.Contains("decal", StringComparison.Ordinal)
            || value.Contains("overlay", StringComparison.Ordinal)
            || value.Contains("roadmark", StringComparison.Ordinal)
            || value.Contains("road_mark", StringComparison.Ordinal);
    }

    private static Vector4 Sample(TextureImage image, Vector2 uv)
    {
        float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
        int x = Math.Clamp((int)(u * image.Width), 0, image.Width - 1);
        int y = Math.Clamp((int)((1f - v) * image.Height), 0, image.Height - 1);
        int o = (y * image.Width + x) * 4;
        return new(image.Rgba[o] / 255f, image.Rgba[o + 1] / 255f, image.Rgba[o + 2] / 255f, image.Rgba[o + 3] / 255f);
    }

    private static string Slug(string value)
    {
        string clean = new(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        while (clean.Contains("__", StringComparison.Ordinal)) clean = clean.Replace("__", "_", StringComparison.Ordinal);
        return clean.Trim('_');
    }

    private static bool Reasonable(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
        && MathF.Abs(value.X) < 10_000_000f && MathF.Abs(value.Y) < 10_000_000f && MathF.Abs(value.Z) < 10_000_000f;

    private sealed class LegacyTextureIndex
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
        public LegacyTextureIndex(string sceneFolder)
        {
            foreach (string file in Directory.EnumerateFiles(sceneFolder, "*", SearchOption.AllDirectories)
                         .Where(p => p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                                  || p.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)))
            {
                _files.TryAdd(Path.GetFileName(file), file);
                _files.TryAdd(Path.GetFileNameWithoutExtension(file), file);
            }
        }
        public bool TryResolve(string reference, out string file)
        {
            string name = Path.GetFileName(reference.Replace('\\', '/'));
            if (_files.TryGetValue(name, out file!)) return true;
            if (_files.TryGetValue(Path.GetFileNameWithoutExtension(name), out file!)) return true;
            string stem = Path.GetFileNameWithoutExtension(name);
            foreach (string ext in new[] { ".dds", ".tga", ".tex" }) if (_files.TryGetValue(stem + ext, out file!)) return true;
            file = ""; return false;
        }
    }

    private readonly record struct SurfaceKey(LegacyMaterialRole Role, string TextureSet, bool DoubleSided);
    private readonly record struct MaterialKey(LegacyMaterialRole Role, string TextureSet);
    private readonly record struct LegacyVertex(
        Vector3 Position, Vector3 Normal, Vector2 Uv, Vector4 Color, Vector3 Pivot, bool HasNormal);

    private sealed class MeshAccumulator
    {
        private readonly Dictionary<long, ushort> _vertices = new();
        public SurfaceKey Key { get; }
        public IReadOnlyDictionary<string, string> Samplers { get; }
        public List<LegacyVertex> Vertices { get; } = new();
        public List<ushort> Indices { get; } = new();
        public int VertexCount => Vertices.Count;
        public int IndexCount => Indices.Count;
        public Vector3 BoundsMin { get; private set; } = new(float.MaxValue);
        public Vector3 BoundsMax { get; private set; } = new(float.MinValue);
        public MeshAccumulator(SurfaceKey key, IReadOnlyDictionary<string, string> samplers) { Key = key; Samplers = new Dictionary<string, string>(samplers); }
        private static long Id(int mesh, int vertex) => ((long)mesh << 32) | (uint)vertex;
        public int NewVertexCount(int mesh, int a, int b, int c)
        {
            int n = 0; long ia = Id(mesh, a), ib = Id(mesh, b), ic = Id(mesh, c);
            if (!_vertices.ContainsKey(ia)) n++;
            if (ib != ia && !_vertices.ContainsKey(ib)) n++;
            if (ic != ia && ic != ib && !_vertices.ContainsKey(ic)) n++;
            return n;
        }
        public void AddTriangle(int mesh, int a, int b, int c, Func<int, LegacyVertex> make)
        {
            Add(a); Add(b); Add(c);
            void Add(int source)
            {
                long id = Id(mesh, source);
                if (!_vertices.TryGetValue(id, out ushort index))
                {
                    index = checked((ushort)Vertices.Count); var vertex = make(source);
                    _vertices[id] = index; Vertices.Add(vertex);
                    BoundsMin = Vector3.Min(BoundsMin, vertex.Position); BoundsMax = Vector3.Max(BoundsMax, vertex.Position);
                }
                Indices.Add(index);
            }
        }
        public void FinishNormals()
        {
            if (Vertices.All(v => v.HasNormal)) return;
            var sums = new Vector3[Vertices.Count];
            for (int i = 0; i + 2 < Indices.Count; i += 3)
            {
                int a = Indices[i], b = Indices[i + 1], c = Indices[i + 2];
                Vector3 n = Vector3.Cross(Vertices[b].Position - Vertices[a].Position, Vertices[c].Position - Vertices[a].Position);
                if (n.LengthSquared() > 1e-12f) { sums[a] += n; sums[b] += n; sums[c] += n; }
            }
            for (int i = 0; i < Vertices.Count; i++)
            {
                Vector3 n = sums[i].LengthSquared() > 1e-12f ? Vector3.Normalize(sums[i]) : Vector3.UnitY;
                Vertices[i] = Vertices[i] with { Normal = n, HasNormal = true };
            }
        }
    }

    private static Vector3[] ReadVector3(VertexElementAccessor accessor, int count)
    {
        var result = new Vector3[count];
        try { var values = accessor.AsVector3Array(); for (int i = 0; i < count; i++) result[i] = values[i]; }
        catch { var values = accessor.AsXyzF16Array(); for (int i = 0; i < count; i++) result[i] = new((float)values[i].Item1, (float)values[i].Item2, (float)values[i].Item3); }
        return result;
    }

    private static Vector2[] ReadVector2(VertexElementAccessor accessor, int count)
    {
        var result = new Vector2[count];
        try { var values = accessor.AsVector2Array(); for (int i = 0; i < count; i++) result[i] = values[i]; }
        catch { var values = accessor.AsXyF16Array(); for (int i = 0; i < count; i++) result[i] = new((float)values[i].Item1, (float)values[i].Item2); }
        return result;
    }

}
