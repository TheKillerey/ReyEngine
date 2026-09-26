using System.Linq;
using System.Numerics;
using ReyEngine.Rendering.D3D11;
using Xunit;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M232: the packing rules for League's particle quads. Each of these encodes a fact measured off
/// <c>particlesystem/quad_vs</c>'s bytecode, and each would fail silently in the image rather than loudly at
/// runtime if it regressed — a swapped colour channel or a dropped frame index just looks slightly wrong.
/// </summary>
public class ParticleQuadBuilderTests
{
    private static float[] OneParticle(
        Vector3 pos, float sizeX, float sizeY, Vector4 rgba, float rot = 0f, float frame = 0f, float erosion = 0f)
    {
        var a = new float[ParticleQuadBuilder.Stride];
        a[ParticleQuadBuilder.OffPos + 0] = pos.X;
        a[ParticleQuadBuilder.OffPos + 1] = pos.Y;
        a[ParticleQuadBuilder.OffPos + 2] = pos.Z;
        a[ParticleQuadBuilder.OffSizeX] = sizeX;
        a[ParticleQuadBuilder.OffSizeY] = sizeY;
        a[ParticleQuadBuilder.OffColor + 0] = rgba.X;
        a[ParticleQuadBuilder.OffColor + 1] = rgba.Y;
        a[ParticleQuadBuilder.OffColor + 2] = rgba.Z;
        a[ParticleQuadBuilder.OffColor + 3] = rgba.W;
        a[ParticleQuadBuilder.OffRot] = rot;
        a[ParticleQuadBuilder.OffFrame] = frame;
        a[ParticleQuadBuilder.OffErosion] = erosion;
        return a;
    }

    private static (PreviewVertex[] V, uint[] I, int VC, int IC) Build(float[] inst, int count = 1)
    {
        var verts = new PreviewVertex[count * 4];
        var idx = new uint[count * 6];
        int v = 0, i = 0;
        var (r, u, n) = ParticleQuadBuilder.Basis(Vector3.UnitZ, Vector3.UnitY);
        ParticleQuadBuilder.Append(inst, count, verts, ref v, idx, ref i, r, u, n);
        return (verts, idx, v, i);
    }

    [Fact]
    public void Colour_is_written_BGRA_because_the_shader_reads_COLOR0_zyxw()
    {
        // quad_vs: mov o1.xyzw, v1.zyxw. Feed a colour whose channels are all distinct so a swap shows.
        var rgba = new Vector4(0.1f, 0.2f, 0.3f, 0.4f);
        var (v, _, _, _) = Build(OneParticle(Vector3.Zero, 1f, 1f, rgba));

        // stored BGRA => x=B(0.3) y=G(0.2) z=R(0.1) w=A(0.4); the shader's .zyxw then yields R,G,B,A again
        Assert.Equal(0.3f, v[0].Color.X, 5);
        Assert.Equal(0.2f, v[0].Color.Y, 5);
        Assert.Equal(0.1f, v[0].Color.Z, 5);
        Assert.Equal(0.4f, v[0].Color.W, 5);

        var roundTrip = new Vector4(v[0].Color.Z, v[0].Color.Y, v[0].Color.X, v[0].Color.W);
        Assert.Equal(rgba, roundTrip);
    }

    [Fact]
    public void Frame_index_and_erosion_drive_ride_in_TEXCOORD0_zw()
    {
        // quad_vs: round_ni r0.x, v2.z  (frame) and, in the ALPHA_EROSION permutation, mov o3.z, v2.w
        var (v, _, _, _) = Build(OneParticle(Vector3.Zero, 1f, 1f, Vector4.One, frame: 7f, erosion: 0.25f));
        for (int k = 0; k < 4; k++)
        {
            Assert.Equal(7f, v[k].Uv0.Z);
            Assert.Equal(0.25f, v[k].Uv0.W);
        }
    }

    [Fact]
    public void Corners_span_the_authored_size_and_face_the_camera()
    {
        var (v, _, _, _) = Build(OneParticle(new Vector3(10f, 20f, 30f), 4f, 2f, Vector4.One));

        // width/height are the authored sizes, centred on the particle position
        float w = Vector3.Distance(v[0].Position, v[1].Position);
        float h = Vector3.Distance(v[1].Position, v[2].Position);
        Assert.Equal(4f, w, 4);
        Assert.Equal(2f, h, 4);

        var centre = (v[0].Position + v[1].Position + v[2].Position + v[3].Position) / 4f;
        Assert.Equal(10f, centre.X, 4);
        Assert.Equal(20f, centre.Y, 4);
        Assert.Equal(30f, centre.Z, 4);

        // and the quad is planar against the view direction
        foreach (var vert in v) Assert.Equal(30f, vert.Position.Z, 4);
    }

    [Fact]
    public void Rotation_spins_the_quad_without_resizing_it()
    {
        var flat = Build(OneParticle(Vector3.Zero, 4f, 4f, Vector4.One)).V;
        var spun = Build(OneParticle(Vector3.Zero, 4f, 4f, Vector4.One, rot: MathF.PI / 4f)).V;

        Assert.Equal(Vector3.Distance(flat[0].Position, flat[1].Position),
                     Vector3.Distance(spun[0].Position, spun[1].Position), 4);
        Assert.NotEqual(flat[0].Position.X, spun[0].Position.X, 3);
    }

    [Fact]
    public void Two_triangles_wind_consistently_and_index_the_right_quad()
    {
        var two = new float[ParticleQuadBuilder.Stride * 2];
        OneParticle(Vector3.Zero, 1f, 1f, Vector4.One).CopyTo(two, 0);
        OneParticle(Vector3.One, 1f, 1f, Vector4.One).CopyTo(two, ParticleQuadBuilder.Stride);

        var (_, i, vc, ic) = Build(two, 2);
        Assert.Equal(8, vc);
        Assert.Equal(12, ic);
        Assert.Equal(new uint[] { 0, 1, 2, 0, 2, 3 }, i[..6].ToArray());
        Assert.Equal(new uint[] { 4, 5, 6, 4, 6, 7 }, i[6..].ToArray());
    }

    [Fact]
    public void Append_stops_at_the_buffer_edge_rather_than_overrunning()
    {
        var many = new float[ParticleQuadBuilder.Stride * 10];
        var verts = new PreviewVertex[4 * 3];        // room for three
        var idx = new uint[6 * 3];
        int v = 0, i = 0;
        var (r, u, n) = ParticleQuadBuilder.Basis(Vector3.UnitZ, Vector3.UnitY);

        int written = ParticleQuadBuilder.Append(many, 10, verts, ref v, idx, ref i, r, u, n);

        Assert.Equal(3, written);
        Assert.Equal(12, v);
        Assert.Equal(18, i);
    }

    [Theory]
    // a 1x1 or unset texDiv must pass the UV through, NOT crop the sprite to a sub-rectangle
    [InlineData(0f, 0f, 1f, 1f, 1f)]
    [InlineData(1f, 1f, 1f, 1f, 1f)]
    [InlineData(4f, 2f, 4f, 0.25f, 0.5f)]
    public void TextureInfo_is_columns_and_their_reciprocals(float dx, float dy, float cols, float invC, float invR)
    {
        var t = ParticleQuadBuilder.TextureInfo(new Vector2(dx, dy));
        Assert.Equal(cols, t[0]);
        Assert.Equal(invC, t[1], 5);
        Assert.Equal(invR, t[2], 5);
    }

    [Fact]
    public void Direction_oriented_points_the_quad_along_screen_space_velocity()
    {
        // GL: rotation = atan(-dot(vel,right), dot(vel,up)). Velocity straight along the camera's up
        // resolves to zero rotation; velocity along its right resolves to a quarter turn. Asserted by
        // comparing against the same particle WITHOUT the mode, which is the only claim that isolates it.
        var alongUp = OneParticle(Vector3.Zero, 1f, 4f, Vector4.One);
        alongUp[ParticleQuadBuilder.OffVel + 1] = 10f;
        var alongRight = OneParticle(Vector3.Zero, 1f, 4f, Vector4.One);
        alongRight[ParticleQuadBuilder.OffVel + 0] = 10f;

        var dir = new ParticleQuadBuilder.QuadOrientation(false, true, default, default, default);
        var plain = BuildWith(OneParticle(Vector3.Zero, 1f, 4f, Vector4.One), default);

        // moving "up" the screen means no turn, so it matches the unrotated quad exactly
        var up = BuildWith(alongUp, dir);
        for (int i = 0; i < 4; i++) Assert.Equal(plain[i].Position.Y, up[i].Position.Y, 4);

        // moving "right" turns it, so it must not
        var side = BuildWith(alongRight, dir);
        Assert.True(Vector3.Distance(plain[0].Position, side[0].Position) > 0.5f,
            $"velocity along the camera right did not turn the quad: {plain[0].Position} -> {side[0].Position}");
    }

    [Fact]
    public void Arbitrary_quad_uses_the_placement_frame_and_ignores_the_spin()
    {
        // A placement frame rotated into the XZ plane must put the quad there, and aRotFrame must be
        // suppressed (GL: `rotation = uArbitraryQuad != 0 ? 0.0 : aRotFrame.x`).
        var inst = OneParticle(Vector3.Zero, 2f, 2f, Vector4.One, rot: 1.234f);
        var orient = new ParticleQuadBuilder.QuadOrientation(
            true, false, Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);

        var v = BuildWith(inst, orient);
        foreach (var vert in v) Assert.Equal(0f, vert.Position.Y, 4);   // flat in XZ, not facing the camera

        // and the suppressed spin means it matches the same quad with rot = 0
        var noSpin = BuildWith(OneParticle(Vector3.Zero, 2f, 2f, Vector4.One), orient);
        for (int i = 0; i < 4; i++)
            Assert.Equal(v[i].Position.X, noSpin[i].Position.X, 4);
    }

    [Fact]
    public void Rotation_scales_along_the_screen_axes_not_the_quad_axes()
    {
        // The GL order is rotate-the-CORNER and only then scale per axis, which has a consequence worth
        // pinning down: a quarter turn maps the corner square onto itself, so the per-axis scales land on
        // the same screen axes as before and the quad's EXTENTS do not change. Rotating the basis instead
        // - what M232 did - would swap them to 1 x 4. This test is the difference between the two.
        var v = BuildWith(OneParticle(Vector3.Zero, 4f, 1f, Vector4.One, rot: MathF.PI / 2f), default);

        float minX = v.Min(x => x.Position.X), maxX = v.Max(x => x.Position.X);
        float minY = v.Min(x => x.Position.Y), maxY = v.Max(x => x.Position.Y);
        Assert.Equal(4f, maxX - minX, 3);
        Assert.Equal(1f, maxY - minY, 3);

        // and the texture IS turned - the corner carrying UV (0,0) moves to a different screen position.
        // Compare the whole vector: under a quarter turn its X happens to be unchanged and only Y flips,
        // so checking one component alone would look like "no rotation".
        var unrotated = BuildWith(OneParticle(Vector3.Zero, 4f, 1f, Vector4.One), default);
        Assert.True(Vector3.Distance(unrotated[0].Position, v[0].Position) > 0.5f,
            $"corner 0 barely moved: {unrotated[0].Position} -> {v[0].Position}");
    }

    private static PreviewVertex[] BuildWith(float[] inst, ParticleQuadBuilder.QuadOrientation o)
    {
        var verts = new PreviewVertex[4];
        var idx = new uint[6];
        int v = 0, i = 0;
        var (r, u, n) = ParticleQuadBuilder.Basis(Vector3.UnitZ, Vector3.UnitY);
        ParticleQuadBuilder.Append(inst, 1, verts, ref v, idx, ref i, r, u, n, o);
        return verts;
    }

    [Fact]
    public void Basis_survives_a_view_direction_parallel_to_up()
    {
        var (r, u, n) = ParticleQuadBuilder.Basis(Vector3.UnitY, Vector3.UnitY);
        Assert.True(r.LengthSquared() > 0.9f);
        Assert.True(u.LengthSquared() > 0.9f);
        Assert.True(MathF.Abs(Vector3.Dot(r, u)) < 1e-4f);
        Assert.True(MathF.Abs(Vector3.Dot(r, n)) < 1e-4f);
    }

    // ---- M773: arbitrary-quad UV mirror + Euler order fixes ----

    /// <summary>A billboard's corner UV is untouched: u = x + 0.5, v = 0.5 - y (the camera-quad mapping,
    /// LTK Manager quad.ts:80). Corner order is (-.5,.5) (.5,.5) (.5,-.5) (-.5,-.5).</summary>
    [Fact]
    public void Billboard_corner_uv_is_the_camera_mapping()
    {
        var (v, _, _, _) = Build(OneParticle(Vector3.Zero, 1f, 1f, Vector4.One));
        Assert.Equal(new Vector2(0f, 0f), new Vector2(v[0].Uv0.X, v[0].Uv0.Y));
        Assert.Equal(new Vector2(1f, 0f), new Vector2(v[1].Uv0.X, v[1].Uv0.Y));
        Assert.Equal(new Vector2(1f, 1f), new Vector2(v[2].Uv0.X, v[2].Uv0.Y));
        Assert.Equal(new Vector2(0f, 1f), new Vector2(v[3].Uv0.X, v[3].Uv0.Y));
    }

    /// <summary>M773: the arbitrary-quad corner UV is mirrored across the anti-diagonal relative to a
    /// billboard's: u = y + 0.5, v = 0.5 - x (LTK Manager quad.ts:21-31), applied to BOTH texture layers
    /// (UV0 and UV1/mult) since the swap happens on the corner, before VfxUvTransform.Cell.</summary>
    [Fact]
    public void Arbitrary_quad_corner_uv_is_mirrored_across_the_anti_diagonal()
    {
        var orient = new ParticleQuadBuilder.QuadOrientation(
            true, false, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        var v = BuildWith(OneParticle(Vector3.Zero, 1f, 1f, Vector4.One), orient);

        Assert.Equal(new Vector2(1f, 1f), new Vector2(v[0].Uv0.X, v[0].Uv0.Y));
        Assert.Equal(new Vector2(1f, 0f), new Vector2(v[1].Uv0.X, v[1].Uv0.Y));
        Assert.Equal(new Vector2(0f, 0f), new Vector2(v[2].Uv0.X, v[2].Uv0.Y));
        Assert.Equal(new Vector2(0f, 1f), new Vector2(v[3].Uv0.X, v[3].Uv0.Y));

        // the mult layer (UV1) rides the same swapped corner
        Assert.Equal(new Vector2(1f, 1f), v[0].Uv1);
        Assert.Equal(new Vector2(0f, 0f), v[2].Uv1);
    }

    /// <summary>The independent reference for the mesh path's Euler order (M765): Z, then X, then Y,
    /// composed as row-vector * (RotZ * RotX * RotY) so RotZ applies first. Mirrors
    /// ShaderPreviewRenderer.DrawRiotMeshInstances' <c>CreateRotationZ * CreateRotationX * CreateRotationY</c>
    /// exactly, so a pass here means the arbitrary-quad basis and the mesh basis cannot disagree.</summary>
    private static Vector3 MeshReferenceRotate(Vector3 v, Vector3 eulerRadians)
    {
        var m = Matrix4x4.CreateRotationZ(eulerRadians.Z)
                * Matrix4x4.CreateRotationX(eulerRadians.X)
                * Matrix4x4.CreateRotationY(eulerRadians.Y);
        return Vector3.Transform(v, m);
    }

    /// <summary>Extracts the arbitrary quad's right/up basis vectors from the four corners of a unit
    /// (sizeX=sizeY=1), unrotated (rot suppressed for arbitrary quads regardless), identity-placement
    /// (Right=X, Up=Y, Forward=Z) quad: corner 1 - corner 0 isolates basisR, corner 0 - corner 3 isolates
    /// basisU (see Corner()'s dx/dy table).</summary>
    private static (Vector3 Right, Vector3 Up) ArbitraryQuadBasis(Vector3 eulerRadians)
    {
        var orient = new ParticleQuadBuilder.QuadOrientation(
            true, false, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        var inst = OneParticle(Vector3.Zero, 1f, 1f, Vector4.One);
        inst[ParticleQuadBuilder.OffEuler + 0] = eulerRadians.X;
        inst[ParticleQuadBuilder.OffEuler + 1] = eulerRadians.Y;
        inst[ParticleQuadBuilder.OffEuler + 2] = eulerRadians.Z;
        var v = BuildWith(inst, orient);
        return (v[1].Position - v[0].Position, v[0].Position - v[3].Position);
    }

    public static System.Collections.Generic.IEnumerable<object[]> EulerTriplesDegrees()
    {
        yield return new object[] { new Vector3(0f, 0f, 0f) };
        yield return new object[] { new Vector3(90f, 0f, -90f) };   // Light1/7/8: ground glows lie flat
        yield return new object[] { new Vector3(0f, 180f, 90f) };  // the Noxus sigil: comes out upright
        yield return new object[] { new Vector3(-90f, 0f, 0f) };
        yield return new object[] { new Vector3(1f, 90f, 90f) };
    }

    [Theory]
    [MemberData(nameof(EulerTriplesDegrees))]
    public void Arbitrary_quad_basis_matches_the_mesh_Z_X_Y_composition(Vector3 degrees)
    {
        var r = degrees * (MathF.PI / 180f);
        var (basisR, basisU) = ArbitraryQuadBasis(r);
        var expectedR = MeshReferenceRotate(Vector3.UnitX, r);
        var expectedU = MeshReferenceRotate(Vector3.UnitY, r);

        Assert.Equal(expectedR.X, basisR.X, 4);
        Assert.Equal(expectedR.Y, basisR.Y, 4);
        Assert.Equal(expectedR.Z, basisR.Z, 4);
        Assert.Equal(expectedU.X, basisU.X, 4);
        Assert.Equal(expectedU.Y, basisU.Y, 4);
        Assert.Equal(expectedU.Z, basisU.Z, 4);
    }

    /// <summary>(90,0,-90) is authored on ground glows (Light1/7/8): under the fixed Z-X-Y order the quad
    /// lies flat (both basis vectors have no world-Y component) instead of standing as an invisible vertical
    /// sheet, which is what the old X-Y-Z order produced.</summary>
    [Fact]
    public void Euler_90_0_neg90_lies_the_quad_flat_facing_up_or_down()
    {
        var (basisR, basisU) = ArbitraryQuadBasis(new Vector3(90f, 0f, -90f) * (MathF.PI / 180f));
        Assert.Equal(0f, basisR.Y, 4);
        Assert.Equal(0f, basisU.Y, 4);
        var normal = Vector3.Cross(basisR, basisU);
        Assert.True(MathF.Abs(normal.Y) > 0.9f, $"expected the normal to face up or down, got {normal}");
    }
}
