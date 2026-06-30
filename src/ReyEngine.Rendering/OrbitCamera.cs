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
    public float Near = 1f;
    public float Far = 200000f;

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

    public void Zoom(float factor) => Distance = Math.Clamp(Distance * factor, 5f, 100000f);

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
        Distance = Math.Clamp(radius / MathF.Tan(FieldOfView * 0.5f) * 1.25f, 10f, 100000f);
        FlySpeed = Math.Max(200f, radius);
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);

    public Matrix4x4 Projection(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect <= 0 ? 1f : aspect, Near, Far);

    public Matrix4x4 ViewProjection(float aspect) => View * Projection(aspect);
}
