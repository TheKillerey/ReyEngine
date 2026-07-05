using System.Collections.Generic;
using System.Numerics;
using ReyEngine.Core.Decoding;

namespace ReyEngine.App.ViewModels;

/// <summary>One submesh of a resolved prop mesh (M41): index range + its decoded diffuse (null = untextured).</summary>
public sealed record PropSubmesh(int Start, int Count, TextureImage? Texture);

/// <summary>
/// A decoded prop mesh (M41), shared by reference across every placement of the same skin so the viewport
/// uploads each geometry + texture only once. Built off the GL thread; uploaded on it.
/// </summary>
public sealed record PropMesh(
    string Key,
    float[] Positions, float[] Normals, float[] Uvs, uint[] Indices,
    IReadOnlyList<PropSubmesh> Submeshes);

/// <summary>One placed prop: a shared mesh at a world transform.</summary>
public sealed record PropInstanceData(PropMesh Mesh, Matrix4x4 Transform);

/// <summary>The full set of placed prop meshes to render for the current map (M41).</summary>
public sealed record PropRenderSet(IReadOnlyList<PropInstanceData> Instances);
