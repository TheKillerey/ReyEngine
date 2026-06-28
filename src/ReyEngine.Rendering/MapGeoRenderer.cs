using System.Numerics;
using Silk.NET.OpenGL;

namespace ReyEngine.Rendering;

/// <summary>Stub for M4 .mapgeo rendering. Kept so the viewport API is stable now.</summary>
public sealed class MapGeoRenderer : IDisposable
{
    public bool IsImplemented => false;

    public void Initialize(GL gl, bool gles) { /* M4 */ }
    public void Render(Matrix4x4 viewProjection) { /* M4 */ }
    public void Dispose() { }
}
