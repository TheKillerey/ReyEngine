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
    public void Basis_survives_a_view_direction_parallel_to_up()
    {
        var (r, u, n) = ParticleQuadBuilder.Basis(Vector3.UnitY, Vector3.UnitY);
        Assert.True(r.LengthSquared() > 0.9f);
        Assert.True(u.LengthSquared() > 0.9f);
        Assert.True(MathF.Abs(Vector3.Dot(r, u)) < 1e-4f);
        Assert.True(MathF.Abs(Vector3.Dot(r, n)) < 1e-4f);
    }
}
