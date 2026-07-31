using System.Numerics;

namespace ReyEngine.Rendering;

/// <summary>
/// Viewport camera with an Unreal-style control set. Internally it keeps an orbit model
/// (Target + Distance + Yaw/Pitch) so framing stays simple, but exposes free-fly + look-in-place
/// operations on top: orbit (Alt+LMB), look (RMB), fly (WASD/QE while RMB), pan (MMB), dolly (wheel).
/// </summary>
public sealed class OrbitCamera
{
    public Vector3 Target = Vector3.Zero;
    public float Distance = 600f;
    public float Yaw = 0.7f;     // radians, around Y
    public float Pitch = 0.5f;   // radians, up/down
    public float FieldOfView = MathF.PI / 4f;
    /// <summary>UPPER BOUND on the near plane, not the near plane itself - see <see cref="EffectiveNear"/>.</summary>
    public float Near = 1f;
    public float Far = 200000f;

    /// <summary>
    /// <para>The near plane actually used, derived from how close the camera currently is.</para>
    ///
    /// <para>M267: it used to be whatever a caller last assigned, and the callers sized it from the
    /// FRAMING distance. Framing a 97,000-unit map put it around 2,000 units and left it there, so flying
    /// in clipped away everything within 2,000 units of the eye - the camera could orbit a map fine and
    /// then refuse to let you approach anything in it. Tying it to Distance instead means it shrinks as
    /// you close in, which is the only thing it was ever meant to do.</para>
    ///
    /// <para>Kept as a MINIMUM against <see cref="Near"/> rather than replacing it, so a caller that
    /// deliberately wants a coarser near plane at range still gets one - it just cannot survive a zoom.
    /// The 0.02 floor keeps the far/near ratio finite when Distance reaches its own floor.</para>
    /// </summary>
    /// <para>M291: the coefficient dropped from 0.01 to 0.0025 and the floor from 0.02 to 0.01, so the
    /// near plane starts shrinking four times further out. At framing range the result is unchanged -
    /// <see cref="Near"/> still caps it at 1 - so distant rendering is untouched; it only bites once the
    /// camera is genuinely close, which is where meshes were being cut away.</para>
    public float EffectiveNear => MathF.Min(Near, MathF.Max(0.01f, Distance * 0.0025f));

    /// <summary>World units per second of WASD fly (user-adjustable via RMB+wheel).</summary>
    public float FlySpeed = 600f;

    // Unit vector from Target to the eye for the current yaw/pitch.
    private Vector3 Dir
    {
        get
        {
            float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
            float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);
            return new Vector3(cp * sy, sp, cp * cy);
        }
    }

    public Vector3 Position => Target + Dir * Distance;
    public Vector3 Forward => Vector3.Normalize(Target - Position); // == -Dir
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));
    public Vector3 Up => Vector3.Cross(Right, Forward);

    /// <summary>Alt+LMB: tumble the eye around the (fixed) target.</summary>
    public void Orbit(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, -1.5f, 1.5f);
    }

    /// <summary>RMB: rotate the view in place (eye stays put, look direction turns).</summary>
    public void Look(float deltaYaw, float deltaPitch)
    {
        var eye = Position;
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, -1.5f, 1.5f);
        Target = eye - Dir * Distance; // keep the eye fixed, move the pivot instead
    }

    /// <summary>WASD/QE fly: translate the whole rig along the camera basis (orientation preserved).</summary>
    public void MoveLocal(float forward, float right, float up, float dt)
    {
        Target += (Forward * forward + Right * right + Vector3.UnitY * up) * (FlySpeed * dt);
    }

    // Floor of 1 rather than 5: with the near plane now tracking Distance there is nothing to protect
    // against down there, and 5 units is a visible standoff on a prop-sized mesh.
    public void Zoom(float factor) => Distance = Math.Clamp(Distance * factor, 1f, 100000f);

    public void AdjustFlySpeed(float factor) => FlySpeed = Math.Clamp(FlySpeed * factor, 20f, 50000f);

    /// <summary>MMB: slide the rig in the view plane.</summary>
    public void Pan(float dx, float dy)
    {
        Target += (Right * -dx + Up * dy) * (Distance * 0.0015f);
    }

    /// <summary>F: frame a bounding sphere (keeps the current viewing angle).</summary>
    public void FocusOn(Vector3 center, float radius)
    {
        Target = center;
        Distance = Math.Clamp(radius / MathF.Tan(FieldOfView * 0.5f) * 1.25f, 1f, 100000f);
        FlySpeed = Math.Max(200f, radius);
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);

    public Matrix4x4 Projection(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect <= 0 ? 1f : aspect, EffectiveNear, Far);

    public Matrix4x4 ViewProjection(float aspect) => View * Projection(aspect);

    /// <summary>
    /// <para>M291: the same frustum in the OPENGL depth convention - clip z spanning [-w, w] rather than
    /// [0, w].</para>
    ///
    /// <para><see cref="Projection"/> comes from <c>Matrix4x4.CreatePerspectiveFieldOfView</c>, and
    /// System.Numerics follows Direct3D: it maps the frustum to z in [0, w]. That is right for D3D11 and
    /// wrong for GL, whose fixed-function clip and <c>glDepthRange</c> both assume [-w, w]. Feeding the
    /// D3D matrix to GL does not clip anything away - [0, w] is a strict subset of [-w, w] - but every
    /// visible fragment then lands in the upper HALF of the depth buffer, throwing away a whole bit of
    /// precision and doubling z-fighting. That cost rises exactly as the near plane shrinks, which is why
    /// it is fixed here alongside the near-plane change rather than separately.</para>
    ///
    /// <para>The usual remedy, <c>glClipControl(ZERO_TO_ONE)</c>, is deliberately NOT used: it is desktop
    /// GL 4.5 and this renderer targets GLES/ANGLE, where it does not exist. Emitting the correct matrix
    /// costs nothing and works everywhere.</para>
    /// </summary>
    public Matrix4x4 ProjectionGl(float aspect)
    {
        float a = aspect <= 0 ? 1f : aspect;
        float n = EffectiveNear, f = Far;
        float h = 1f / MathF.Tan(FieldOfView * 0.5f);
        var m = Matrix4x4.Identity;
        m.M11 = h / a;
        m.M22 = h;
        // The only rows that differ from the D3D form: D3D uses f/(n-f) and n*f/(n-f).
        m.M33 = (f + n) / (n - f);
        m.M34 = -1f;
        m.M43 = 2f * f * n / (n - f);
        m.M44 = 0f;
        return m;
    }

    public Matrix4x4 ViewProjectionGl(float aspect) => View * ProjectionGl(aspect);
}
