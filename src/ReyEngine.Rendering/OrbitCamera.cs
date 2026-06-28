using System.Numerics;

namespace ReyEngine.Rendering;

/// <summary>Simple orbit/turntable camera around a target point.</summary>
public sealed class OrbitCamera
{
    public Vector3 Target = Vector3.Zero;
    public float Distance = 600f;
    public float Yaw = 0.7f;     // radians, around Y
    public float Pitch = 0.5f;   // radians, up/down
    public float FieldOfView = MathF.PI / 4f;
    public float Near = 1f;
    public float Far = 200000f;

    public void Orbit(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, -1.5f, 1.5f);
    }

    public void Zoom(float factor) => Distance = Math.Clamp(Distance * factor, 5f, 100000f);

    public void Pan(float dx, float dy)
    {
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, Forward));
        var up = Vector3.Cross(Forward, right);
        Target += (right * dx + up * dy) * (Distance * 0.001f);
    }

    public Vector3 Forward => Vector3.Normalize(Target - Position);

    public Vector3 Position
    {
        get
        {
            float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
            float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);
            return Target + new Vector3(cp * sy, sp, cp * cy) * Distance;
        }
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);

    public Matrix4x4 Projection(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect <= 0 ? 1f : aspect, Near, Far);

    public Matrix4x4 ViewProjection(float aspect) => View * Projection(aspect);
}
