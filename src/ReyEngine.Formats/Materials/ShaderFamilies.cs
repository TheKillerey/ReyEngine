namespace ReyEngine.Formats.Materials;

/// <summary>
/// M701: which shaders a material can actually be given, by what the material draws.
///
/// <para><b>Measured, and close to absolute.</b> Character skin bins across 20 champion WADs: 2,235 of
/// 2,237 materials use a <c>Shaders/SkinnedMesh/</c> shader and the other two use a UI shader - not one
/// uses a StaticMesh shader. The three shipping map WADs: 5,870 of 5,870 map materials use
/// <c>Shaders/StaticMesh/</c>. The two families are not interchangeable, because they are fed by
/// different draw paths: a skinned draw supplies bone matrices, a static draw supplies a world matrix,
/// and binding the wrong one is the client error "Missing shader constant WORLD_MATRIX" with nothing
/// drawn.</para>
///
/// <para>So the editor offers the family that fits and says so. It does not REFUSE the other one: two
/// shipped character materials really are on a UI shader, and a hard rule would be the editor claiming
/// to know better than the data. A shader from the wrong family is applied with a warning that names the
/// consequence.</para>
/// </summary>
public static class ShaderFamilies
{
    public const string Skinned = "Shaders/SkinnedMesh/";
    public const string Static = "Shaders/StaticMesh/";

    /// <summary>The prefix a material of this kind is drawn with.</summary>
    public static string PrefixFor(MaterialSourceKind kind) =>
        kind == MaterialSourceKind.ChampionSkin ? Skinned : Static;

    public static string NameFor(MaterialSourceKind kind) =>
        kind == MaterialSourceKind.ChampionSkin ? "skinned-mesh" : "static-mesh";

    public static bool Fits(string? shader, MaterialSourceKind kind) =>
        !string.IsNullOrWhiteSpace(shader)
        && shader.Trim().StartsWith(PrefixFor(kind), StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the shader suits the material; otherwise the line to show the user.</summary>
    public static string? Warning(string? shader, MaterialSourceKind kind)
    {
        if (string.IsNullOrWhiteSpace(shader) || Fits(shader, kind)) return null;
        string other = kind == MaterialSourceKind.ChampionSkin ? Static : Skinned;
        string mesh = kind == MaterialSourceKind.ChampionSkin ? "a character" : "map geometry";
        string need = kind == MaterialSourceKind.ChampionSkin ? "bone matrices" : "a world matrix";
        return shader.Trim().StartsWith(other, StringComparison.OrdinalIgnoreCase)
            ? $"That is a {NameFor(kind == MaterialSourceKind.ChampionSkin ? MaterialSourceKind.MapMaterials : MaterialSourceKind.ChampionSkin)} "
              + $"shader on {mesh}. The draw supplies {need}, which that shader does not take - in game it reports a missing "
              + "shader constant and draws nothing. Riot ships zero materials like this."
            : $"That shader is outside the {NameFor(kind)} family {mesh} draws with. Check it in game before shipping it.";
    }

    /// <summary>The catalogue narrowed to what this kind can draw, with anything the file already uses
    /// kept whatever family it is in - the editor never hides a shader the document itself chose.</summary>
    public static IEnumerable<string> Offer(IEnumerable<string> catalogue, IEnumerable<string> alreadyUsed, MaterialSourceKind kind)
    {
        string prefix = PrefixFor(kind);
        var used = new HashSet<string>(alreadyUsed, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
        foreach (var name in catalogue)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(name))
                yield return name;
        foreach (var name in used) yield return name;
    }
}
