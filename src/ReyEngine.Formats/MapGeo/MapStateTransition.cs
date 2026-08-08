namespace ReyEngine.Formats.MapGeo;

/// <summary>
/// M394: the environment crossfade between two map states, as Riot drives it.
///
/// <para>Mechanism, read off <c>staticmesh/vertexdeform.ps.dx11</c> blob 19 rather than invented:</para>
/// <code>
///   sample r2, t1   // GRASS_TINT_MAP_ALTERNATE  - the INCOMING state
///   sample r1, t0   // GRASS_TINT_MAP            - the CURRENT state
///   add   r2, -r1, r2
///   mad   r1, cb1[16].yyyy, r2, r1        // base + interp*(alt-base)
/// </code>
/// <para><c>cb1[16].y</c> is PerFramePixelCB offset 260 = <c>GRASS_INTERP</c>, and the RDEF marks it USED
/// (its neighbours LIGHT_GRID_TEXTURE_SCALE and ENV_BRIGHTNESS are [unused]). So the crossfade is a
/// hardware lerp between two bound textures, and the only thing an implementation has to supply is the
/// factor and the pair of textures.</para>
///
/// <para>Duration comes from <see cref="MapVisibilityFlagDefinition.TransitionTime"/> — Riot's own
/// seconds, per target state. A state that authors none (Hextech, Chemtech) transitions INSTANTLY; that
/// is what the data says and no default is substituted for it.</para>
///
/// <para>Renderer-agnostic on purpose: OpenGL and D3D11 read the same <see cref="Interp"/> and the same
/// two paths, and differ only in how they hand them to the GPU.</para>
/// </summary>
public sealed class MapStateTransition
{
    /// <summary>The texture being faded FROM. Bound to GRASS_TINT_MAP.</summary>
    public string? FromPath { get; private set; }

    /// <summary>The texture being faded TO. Bound to GRASS_TINT_MAP_ALTERNATE.</summary>
    public string? ToPath { get; private set; }

    /// <summary>Riot's authored seconds for this transition. Zero means instant.</summary>
    public float Duration { get; private set; }

    public float Elapsed { get; private set; }

    /// <summary>GRASS_INTERP: 0 = fully FromPath, 1 = fully ToPath. Always 1 for an instant transition,
    /// so a caller that only ever reads Interp still lands on the right texture.</summary>
    public float Interp => Duration <= 0f ? 1f : Math.Clamp(Elapsed / Duration, 0f, 1f);

    /// <summary>True while the fade is still moving. False before anything starts and once it lands.</summary>
    public bool IsRunning => Duration > 0f && Elapsed < Duration;

    /// <summary>What this fade settles on — also what a renderer with no alternate slot should bind.</summary>
    public string? SettledPath => ToPath;

    /// <summary>
    /// The single texture closest to what is currently on screen. Used as the from-side when a fade is
    /// interrupted.
    ///
    /// <para>An APPROXIMATION, and deliberately so: mid-fade the screen shows a blend of two textures,
    /// and there is nowhere to bind a blend — the shader has exactly two slots and one factor. Snapping
    /// to the nearer end keeps the discontinuity at its smallest (at worst half a fade) instead of always
    /// jumping back to the original, which is what clicking quickly through dragon states would expose.</para>
    /// </summary>
    public string? NearestPath => Interp >= 0.5f ? ToPath : FromPath;

    /// <summary>
    /// Start a fade to <paramref name="toPath"/>.
    ///
    /// <para>Interrupting a running fade starts a NEW one from wherever it currently is — the from-side
    /// becomes the texture that was already showing, not the original base. Restarting from the base
    /// would visibly jump backwards when a state is changed twice quickly, which is exactly what a user
    /// clicking through dragon states does.</para>
    /// </summary>
    /// <param name="durationSeconds">Riot's TransitionTime, or null when the state authors none —
    /// which means instant, not "use a default".</param>
    /// <returns>False when this is not a change (already showing that texture), so callers can skip
    /// rebinding.</returns>
    public bool Begin(string? toPath, float? durationSeconds)
    {
        // No-op only when the fade has LANDED on that texture. A running fade heading there is not there
        // yet, so re-issuing it still has to restart from the current point.
        if (string.Equals(ToPath, toPath, StringComparison.OrdinalIgnoreCase) && !IsRunning) return false;

        FromPath = NearestPath;
        ToPath = toPath;
        Duration = durationSeconds is { } d && d > 0f ? d : 0f;
        Elapsed = 0f;
        return true;
    }

    /// <summary>Advance by a frame. Negative or zero deltas are ignored rather than rewinding.</summary>
    public void Advance(float deltaSeconds)
    {
        if (deltaSeconds <= 0f || !IsRunning) return;
        Elapsed = Math.Min(Elapsed + deltaSeconds, Duration);
    }

    /// <summary>Collapse to the finished state: the incoming texture becomes the current one and the
    /// factor returns to 0. Called once a fade lands so the next one starts from a clean pair.</summary>
    public void Settle()
    {
        FromPath = ToPath;
        Duration = 0f;
        Elapsed = 0f;
    }

    /// <summary>Drop any fade and show <paramref name="path"/> immediately — used when the map changes
    /// under us, where fading from the previous map's texture would be meaningless.</summary>
    public void ResetTo(string? path)
    {
        FromPath = ToPath = path;
        Duration = 0f;
        Elapsed = 0f;
    }

    public override string ToString() => IsRunning
        ? $"{FromPath} -> {ToPath}  {Interp:P0} of {Duration:0.##}s"
        : $"{SettledPath} (settled)";
}
