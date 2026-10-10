using System.Text;

namespace ReyEngine.Core.Build;

/// <summary>
/// M833: a material that names a Shader Graph (<c>REY_GRAPH</c>) is only safe to ship together with the shader cache patch that gives it twin
/// keys, and only folder-project exports generate that. Every other path that writes bin bytes into a package must refuse such bytes.
/// </summary>
public static class ShaderGraphShipGuard
{
    private static readonly byte[] Needle = Encoding.ASCII.GetBytes("REY_GRAPH");

    public static bool Mentions(byte[] data) => data.AsSpan().IndexOf(Needle) >= 0;

    public static string Message(string what) =>
        $"{what} names a Shader Graph (REY_GRAPH), and Shader Graphs ship only from folder projects via Export .fantome or Build Package: this path would ship the material without the shader cache patch, and the game would not find its shader. Unassign the graph or export from a folder project.";
}
