namespace ReyEngine.Rendering.D3D11;

/// <summary>
/// M840: which engine-owned textures a shader may find UNBOUND and what "nothing there" must read as.
///
/// <para>White is the right stand-in for almost every texture - it multiplies out. It is the WRONG one for the
/// two shared map textures below, because the game's own shaders spend them as <c>1 - x</c>:</para>
/// <list type="bullet">
/// <item><c>CLOUD_CARDS_SharedTexture</c>: <c>sun *= (1 - cloud.rgb)</c>. Census of the installed 16.20
/// ShaderCache.dx11: 224 pixel-shader families sample it (218 skinned lit shaders, 4 viego, 1 items,
/// mantis_env_baked_pbr) and every one of them does the 1 - x on the very next instruction. White makes the
/// sun zero everywhere; zero (no cloud shadow) leaves it untouched.</item>
/// <item><c>GAMEPLAY_TEXTURE_SharedTexture</c>: Mantis only (24 pixel permutations). It drives the Void overlay
/// with <c>1 - gameplay.x</c> (then scaled and offset), so white turns the void ON across the whole map - the
/// purple/grey swirl; zero is "no gameplay-state data" and leaves it off. The .w read is gated behind a debug
/// view in cb2.</item>
/// </list>
/// </summary>
public static class StandInPolicy
{
    /// <summary>True when an unbound 2D texture of this reflected name must read as (0,0,0,0).</summary>
    public static bool ReadsAsZero(string reflectedName) =>
        reflectedName.Equals("CLOUD_CARDS_SharedTexture", StringComparison.OrdinalIgnoreCase)
        || reflectedName.Equals("GAMEPLAY_TEXTURE_SharedTexture", StringComparison.OrdinalIgnoreCase);
}
