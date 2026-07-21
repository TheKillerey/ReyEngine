using System.Numerics;
using Silk.NET.Assimp;

namespace ReyEngine.Formats.Meshes;

/// <summary>One mesh from an imported scene file, baked to world space, ready for the mapgeo appender.</summary>
public sealed record ImportedSceneMesh(
    string Name,
    string MaterialName,
    float[] Positions,   // 3 / vertex, world-space (node transforms applied)
    float[] Normals,
    float[] Uvs,
    int[] Indices);

/// <summary>One material referenced by the imported meshes, with its diffuse texture when the scene
/// carries one: either a path (relative to the scene file, may not exist) or, for GLB embeds
/// ("*0" references), the compressed image bytes (png/jpg) ready to decode.</summary>
public sealed record ImportedSceneMaterial(string Name, string? DiffuseTexturePath, byte[]? EmbeddedTexture = null)
{
    public bool HasTexture => EmbeddedTexture is not null || !string.IsNullOrEmpty(DiffuseTexturePath);
}

public sealed record ImportedScene(
    IReadOnlyList<ImportedSceneMesh> Meshes,
    IReadOnlyList<ImportedSceneMaterial> Materials);

/// <summary>
/// M123: imports .fbx / .glb / .gltf (and everything else Assimp reads) for the Add Mesh window.
/// Triangulated, node transforms baked into vertices, one entry per mesh with its material name so
/// the window can offer per-material setup. Never throws - null + error out on failure.
/// </summary>
public static class SceneMeshImporter
{
    public static unsafe ImportedScene? Import(string path, out string? error)
    {
        error = null;
        var api = Assimp.GetApi();
        Scene* scene = null;
        try
        {
            scene = api.ImportFile(path, (uint)(
                PostProcessSteps.Triangulate
                | PostProcessSteps.JoinIdenticalVertices
                | PostProcessSteps.GenerateSmoothNormals
                | PostProcessSteps.FlipUVs));   // GL-style V origin -> mapgeo expects D3D-style, flip back below
            if (scene is null || scene->MRootNode is null)
            {
                error = api.GetErrorStringS();
                if (string.IsNullOrEmpty(error)) error = "Assimp returned no scene.";
                return null;
            }

            // materials
            var materials = new List<ImportedSceneMaterial>();
            for (uint i = 0; i < scene->MNumMaterials; i++)
            {
                Material* mat = scene->MMaterials[i];
                string name = GetMaterialString(api, mat, Assimp.MaterialNameBase) ?? $"Material_{i}";
                string? diffuse = GetMaterialTexture(api, mat, TextureType.Diffuse)
                                  ?? GetMaterialTexture(api, mat, TextureType.BaseColor);

                // "*N" = embedded texture (GLB packs images into the scene) - pull the compressed bytes
                byte[]? embedded = null;
                if (diffuse is ['*', ..] && uint.TryParse(diffuse.AsSpan(1), out var ti) && ti < scene->MNumTextures)
                {
                    Texture* t = scene->MTextures[ti];
                    if (t->MHeight == 0 && t->MWidth > 0)   // compressed blob (png/jpg); raw texel embeds are rare
                    {
                        embedded = new byte[t->MWidth];
                        new Span<byte>(t->PcData, (int)t->MWidth).CopyTo(embedded);
                    }
                    diffuse = null;
                }
                materials.Add(new ImportedSceneMaterial(name, diffuse, embedded));
            }

            var meshes = new List<ImportedSceneMesh>();
            Walk(scene->MRootNode, Matrix4x4.Identity);
            void Walk(Node* node, Matrix4x4 parent)
            {
                // assimp matrices are row-major with translation in the last COLUMN -> transpose for System.Numerics
                var world = Matrix4x4.Transpose(node->MTransformation) * parent;
                for (uint m = 0; m < node->MNumMeshes; m++)
                {
                    Mesh* mesh = scene->MMeshes[node->MMeshes[m]];
                    if (mesh->MNumVertices == 0 || mesh->MNumFaces == 0) continue;

                    int vc = (int)mesh->MNumVertices;
                    var pos = new float[vc * 3];
                    var nrm = new float[vc * 3];
                    var uv = new float[vc * 2];
                    bool hasNormalMatrix = Matrix4x4.Invert(world, out var inv);
                    var normalM = hasNormalMatrix ? Matrix4x4.Transpose(inv) : world;
                    for (int v = 0; v < vc; v++)
                    {
                        var p = Vector3.Transform(mesh->MVertices[v], world);
                        pos[v * 3] = p.X; pos[v * 3 + 1] = p.Y; pos[v * 3 + 2] = p.Z;
                        if (mesh->MNormals is not null)
                        {
                            var n = Vector3.Normalize(Vector3.TransformNormal(mesh->MNormals[v], normalM));
                            nrm[v * 3] = n.X; nrm[v * 3 + 1] = n.Y; nrm[v * 3 + 2] = n.Z;
                        }
                        if (mesh->MTextureCoords[0] is not null)
                        {
                            var t = mesh->MTextureCoords[0][v];
                            uv[v * 2] = t.X; uv[v * 2 + 1] = 1f - t.Y;   // undo FlipUVs -> D3D convention
                        }
                    }

                    var idx = new List<int>((int)mesh->MNumFaces * 3);
                    for (uint f = 0; f < mesh->MNumFaces; f++)
                    {
                        var face = mesh->MFaces[f];
                        if (face.MNumIndices != 3) continue;   // lines/points post-triangulation
                        idx.Add((int)face.MIndices[0]);
                        idx.Add((int)face.MIndices[1]);
                        idx.Add((int)face.MIndices[2]);
                    }
                    if (idx.Count < 3) continue;

                    string matName = mesh->MMaterialIndex < materials.Count
                        ? materials[(int)mesh->MMaterialIndex].Name : "";
                    string meshName = mesh->MName.AsString;
                    if (string.IsNullOrEmpty(meshName)) meshName = $"mesh_{meshes.Count}";
                    meshes.Add(new ImportedSceneMesh(meshName, matName, pos, nrm, uv, idx.ToArray()));
                }
                for (uint ch = 0; ch < node->MNumChildren; ch++) Walk(node->MChildren[ch], world);
            }

            if (meshes.Count == 0) { error = "The file contains no triangle meshes."; return null; }

            // only report materials that are actually used by an imported mesh
            var used = meshes.Select(m => m.MaterialName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new ImportedScene(meshes, materials.Where(m => used.Contains(m.Name)).ToList());
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            if (scene is not null) api.ReleaseImport(scene);
        }
    }

    private static unsafe string? GetMaterialString(Assimp api, Material* mat, string key)
    {
        AssimpString value = default;
        return api.GetMaterialString(mat, key, 0, 0, ref value) == Return.Success && value.Length > 0
            ? value.AsString : null;
    }

    private static unsafe string? GetMaterialTexture(Assimp api, Material* mat, TextureType type)
    {
        if (api.GetMaterialTextureCount(mat, type) == 0) return null;
        AssimpString path = default;
        return api.GetMaterialTexture(mat, type, 0, ref path, null, null, null, null, null, null) == Return.Success
               && path.Length > 0 ? path.AsString : null;
    }
}
