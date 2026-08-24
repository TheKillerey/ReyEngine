using System.Net.Sockets;
using System.Numerics;
using System.Text.Json;
using ReyEngine.App.Services;
using ReyEngine.Formats.Interop;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M580: the link over a real socket.
///
/// <para>The codec is covered elsewhere; what this exercises is the part that only breaks in company —
/// framing a binary body onto a stream, a client that connects and leaves, and the handler hop onto the
/// editor's thread. A protocol that round-trips in memory can still deadlock or desync on a socket.</para>
/// </summary>
public sealed class BlenderBridgeServerTests : IDisposable
{
    private readonly BlenderBridgeServer _server;

    public BlenderBridgeServerTests()
    {
        var mesh = new BridgeMesh(4, "sru_rock", new Vector3(1, 2, 3),
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, null, new uint[] { 0, 1, 2 },
            new Vector3(1, 2, 3), Vector3.Zero, Vector3.One);

        // Handlers run inline: there is no dispatcher in a test, and the point here is the wire.
        _server = new BlenderBridgeServer(action => action(), (_, _) => { })
        {
            Handlers = new BlenderBridgeHandlers(
                MapName: () => "jade_container.mapgeo",
                Pull: () => new BridgeSnapshot(new[] { mesh }, new[] { "a note" }),
                PushTransforms: t => $"{t.Count} applied, 0 refused"),
        };
    }

    public void Dispose() => _server.Dispose();

    /// <summary>Port 0 lets the OS pick, so a developer already running the editor does not fail the suite.</summary>
    private int Start()
    {
        Assert.True(_server.Start(0, out var error), error);
        return _server.Port;
    }

    private static (JsonElement Header, byte[] Body) Round(int port, string request, bool wantBody)
    {
        using var client = new TcpClient();
        client.Connect(System.Net.IPAddress.Loopback, port);
        using var stream = client.GetStream();

        BlenderBridgeProtocol.WriteMessage(stream,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["op"] = "hello", ["protocol"] = BlenderBridgeProtocol.Version }));
        BlenderBridgeProtocol.ReadHeader(stream);

        BlenderBridgeProtocol.WriteMessage(stream, request);
        string header = BlenderBridgeProtocol.ReadHeader(stream) ?? throw new IOException("no reply");
        var parsed = JsonDocument.Parse(header).RootElement;
        byte[] body = wantBody
            ? BlenderBridgeProtocol.ReadExactly(stream, parsed.GetProperty("bytes").GetInt32())
            : Array.Empty<byte>();
        return (parsed.Clone(), body);
    }

    [Fact]
    public void AClientCanPullTheMapOverASocket()
    {
        int port = Start();
        var (header, body) = Round(port, "{\"op\":\"pull\"}", wantBody: true);

        Assert.Equal("meshes", header.GetProperty("op").GetString());
        Assert.Equal(1, header.GetProperty("count").GetInt32());
        Assert.Equal("a note", header.GetProperty("notes")[0].GetString());

        var mesh = Assert.Single(BlenderBridgeProtocol.DecodeMeshes(body));
        Assert.Equal("sru_rock", mesh.Name);
        Assert.Equal(4, mesh.Index);
        Assert.Equal(new Vector3(1, 2, 3), mesh.Pivot);
    }

    [Fact]
    public void APushReachesTheEditorAndIsAcknowledged()
    {
        int port = Start();
        var (header, _) = Round(port,
            "{\"op\":\"push\",\"transforms\":[{\"i\":4,\"loc\":[1,2,3],\"rot\":[0,90,0],\"scl\":[1,1,1]}]}",
            wantBody: false);

        Assert.Equal("ok", header.GetProperty("op").GetString());
        Assert.Equal(1, header.GetProperty("applied").GetInt32());
        Assert.Contains("applied", header.GetProperty("detail").GetString());
    }

    [Fact]
    public void AnOlderAddOnIsTurnedAwayRatherThanMisread()
    {
        // A version skew does not fail loudly on its own - it decodes into plausible nonsense.
        int port = Start();
        using var client = new TcpClient();
        client.Connect(System.Net.IPAddress.Loopback, port);
        using var stream = client.GetStream();

        BlenderBridgeProtocol.WriteMessage(stream, "{\"op\":\"hello\",\"protocol\":0}");
        var reply = JsonDocument.Parse(BlenderBridgeProtocol.ReadHeader(stream)!).RootElement;
        Assert.Equal("error", reply.GetProperty("op").GetString());
        Assert.Contains("protocol", reply.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheServerKeepsServingAfterAClientLeaves()
    {
        // Blender opens a fresh connection per action, so a link that only survives one is no link.
        int port = Start();
        for (int i = 0; i < 3; i++)
        {
            var (header, _) = Round(port, "{\"op\":\"pull\"}", wantBody: true);
            Assert.Equal(1, header.GetProperty("count").GetInt32());
        }
        Assert.True(_server.IsRunning);
    }

    [Fact]
    public void AnUnknownRequestIsAnsweredInsteadOfIgnored()
    {
        // A silent drop would leave the add-on blocked on a read until its timeout.
        int port = Start();
        var (header, _) = Round(port, "{\"op\":\"teleport\"}", wantBody: false);
        Assert.Equal("error", header.GetProperty("op").GetString());
    }

    [Fact]
    public void TheLinkOnlyEverListensOnLoopback()
    {
        // It hands out a map's whole geometry and accepts edits to it with no authentication of any kind.
        int port = Start();
        string source = File.ReadAllText(ServerSource());
        Assert.Contains("IPAddress.Loopback", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IPAddress.Any", source, StringComparison.Ordinal);
        Assert.True(port > 0);
    }

    private static string ServerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "ReyEngine.App", "Services", "BlenderBridgeServer.cs");
    }
}
