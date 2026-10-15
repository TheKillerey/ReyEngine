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
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, null, null, new uint[] { 0, 1, 2 },
            new Vector3(1, 2, 3), Vector3.Zero, Vector3.One);

        // Handlers run inline: there is no dispatcher in a test, and the point here is the wire.
        _server = new BlenderBridgeServer(action => action(), (_, _) => { })
        {
            Handlers = new BlenderBridgeHandlers(
                MapName: () => "jade_container.mapgeo",
                Pull: () => new BridgeSnapshot(new[] { mesh }, new[] { "a note" })
                {
                    MeshInfo = new[] { new Dictionary<string, object?> { ["i"] = 4, ["visibility"] = 1 } },
                    MapInfo = new Dictionary<string, object?> { ["lightmap_color_scale"] = 1.5 },
                },
                PushTransforms: t => { Pushed += t.Count; return $"{t.Count} applied, 0 refused"; },
                PushGeometry: m => $"{m.Count} queued, 0 refused")
            {
                HelloInfo = () => new Dictionary<string, object?> { ["project"] = "Test project", ["map_path"] = "data/maps/x.mapgeo" },
                Materials = () => new Dictionary<string, object?>
                {
                    ["count"] = 1,
                    ["materials"] = new Dictionary<string, object?> { ["Rock_MAT"] = new Dictionary<string, object?> { ["name"] = "Rock_MAT" } },
                },
                Texture = path => path.EndsWith("rock.tex", StringComparison.Ordinal)
                    ? new BridgeTexture(true, path, 0x1234, ".tex", "game", new byte[] { (byte)'T', (byte)'E', (byte)'X', 0, 9, 9 }, null)
                    : new BridgeTexture(false, path, 0, "", "", null, "not in the project or the game"),
                AssignMaterial = list =>
                {
                    Assigned.AddRange(list);
                    var refused = list.Select((a, i) => (a, i)).Where(x => x.a.Material == "nope")
                        .Select(x => new BridgeRefusal(x.i, $"mesh {x.a.Mesh}", "'nope' is not a material of this map")).ToList();
                    return new BridgeEditReply(list.Count - refused.Count, refused, "assigned");
                },
                EditMaterial = list =>
                {
                    if (list.Any(e => e.Material == "boom")) throw new InvalidOperationException("the editor fell over");
                    Edited.AddRange(list);
                    return new BridgeEditReply(list.Count, Array.Empty<BridgeRefusal>(), "edited");
                },
            },
        };
    }

    public List<BridgeAssignment> Assigned { get; } = new();
    public int Pushed;
    public List<BridgeMaterialEdit> Edited { get; } = new();

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

    // ---- M841: protocol 3 ----------------------------------------------------------------------

    private static (JsonElement Header, byte[] Body) Send(NetworkStream stream, string request, byte[]? body = null)
    {
        BlenderBridgeProtocol.WriteMessage(stream, request, body);
        string header = BlenderBridgeProtocol.ReadHeader(stream) ?? throw new IOException("no reply");
        var parsed = JsonDocument.Parse(header).RootElement.Clone();
        int bytes = parsed.TryGetProperty("bytes", out var b) ? b.GetInt32() : 0;
        return (parsed, bytes > 0 ? BlenderBridgeProtocol.ReadExactly(stream, bytes) : Array.Empty<byte>());
    }

    private static NetworkStream Connect(int port, TcpClient client)
    {
        client.Connect(System.Net.IPAddress.Loopback, port);
        return client.GetStream();
    }

    [Fact]
    public void TheOldBridgeAddOnIsToldItWasReplacedByMapgeoAddon()
    {
        int port = Start();
        using var client = new TcpClient();
        var stream = Connect(port, client);
        var (reply, _) = Send(stream, "{\"op\":\"hello\",\"protocol\":2}");
        Assert.Equal("error", reply.GetProperty("op").GetString());
        string message = reply.GetProperty("message").GetString()!;
        Assert.Contains("MapgeoAddon", message);
        Assert.Contains("replaced", message);

        // and the connection is over: nothing else is read from a client whose hello was refused
        Assert.True(Closed(stream));
    }

    [Fact]
    public void HelloAnswersWithTheProjectAndTheMap()
    {
        int port = Start();
        using var client = new TcpClient();
        var stream = Connect(port, client);
        var (hello, _) = Send(stream, "{\"op\":\"hello\",\"protocol\":5,\"client\":\"MapgeoAddon 0.5.0\"}");
        Assert.Equal("hello", hello.GetProperty("op").GetString());
        Assert.Equal(5, hello.GetProperty("protocol").GetInt32());
        Assert.Equal("Test project", hello.GetProperty("project").GetString());
        Assert.Equal("jade_container.mapgeo", hello.GetProperty("map").GetString());
        Assert.Equal("MapgeoAddon 0.5.0", _server.ClientName);
    }

    [Fact]
    public void OneConnectionServesPullMaterialsTexturesAssignmentsAndEdits()
    {
        int port = Start();
        using var client = new TcpClient();
        var stream = Connect(port, client);
        Send(stream, "{\"op\":\"hello\",\"protocol\":5}");

        var (meshes, body) = Send(stream, "{\"op\":\"pull\"}");
        Assert.Equal(1, meshes.GetProperty("count").GetInt32());
        Assert.Equal(4, meshes.GetProperty("meshes")[0].GetProperty("i").GetInt32());
        Assert.Equal(1.5, meshes.GetProperty("lightmap_color_scale").GetDouble());
        Assert.Single(BlenderBridgeProtocol.DecodeMeshes(body));

        var (materials, _) = Send(stream, "{\"op\":\"materials\"}");
        Assert.Equal("materials", materials.GetProperty("op").GetString());
        Assert.Equal("Rock_MAT", materials.GetProperty("materials").GetProperty("Rock_MAT").GetProperty("name").GetString());

        var (texture, bytes) = Send(stream, "{\"op\":\"texture\",\"path\":\"assets/maps/rock.tex\"}");
        Assert.True(texture.GetProperty("found").GetBoolean());
        Assert.Equal(".tex", texture.GetProperty("ext").GetString());
        Assert.Equal("0x0000000000001234", texture.GetProperty("link").GetString());
        Assert.Equal(6, bytes.Length);
        var (missing, none) = Send(stream, "{\"op\":\"texture\",\"path\":\"assets/maps/gone.tex\"}");
        Assert.False(missing.GetProperty("found").GetBoolean());
        Assert.Empty(none);

        var (assigned, _) = Send(stream, "{\"op\":\"assign_material\",\"assignments\":[{\"mesh\":4,\"submesh\":0,\"material\":\"Rock_MAT\"},{\"mesh\":4,\"material\":\"nope\"}]}");
        Assert.Equal(1, assigned.GetProperty("applied").GetInt32());
        Assert.Equal(1, assigned.GetProperty("refused")[0].GetProperty("index").GetInt32());
        Assert.Equal(2, Assigned.Count);

        var (edited, _) = Send(stream, "{\"op\":\"edit_material\",\"edits\":[{\"material\":\"Rock_MAT\",\"kind\":\"param\",\"name\":\"TintColor\",\"value\":[1,0,0,1]}]}");
        Assert.Equal(1, edited.GetProperty("applied").GetInt32());
        Assert.Equal("TintColor", Assert.Single(Edited).Name);
    }

    [Fact]
    public void AnEditorThatThrowsIsAnsweredWithAnErrorAndTheConnectionStaysUsable()
    {
        int port = Start();
        using var client = new TcpClient();
        var stream = Connect(port, client);
        Send(stream, "{\"op\":\"hello\",\"protocol\":5}");

        var (failed, _) = Send(stream, "{\"op\":\"edit_material\",\"edits\":[{\"material\":\"boom\",\"kind\":\"switch\",\"name\":\"S\",\"on\":true}]}");
        Assert.Equal("error", failed.GetProperty("op").GetString());
        Assert.Contains("fell over", failed.GetProperty("message").GetString());

        // an unknown op that carries a body: the body is skipped, so the next request is read in step
        var (unknown, _) = Send(stream, "{\"op\":\"teleport\",\"bytes\":4}", new byte[] { 1, 2, 3, 4 });
        Assert.Equal("error", unknown.GetProperty("op").GetString());
        var (garbage, _) = Send(stream, "not json at all");
        Assert.Equal("error", garbage.GetProperty("op").GetString());

        var (pull, _) = Send(stream, "{\"op\":\"pull\"}");
        Assert.Equal("meshes", pull.GetProperty("op").GetString());
    }

    [Fact]
    public void AHostWithoutTheMaterialOperationsAnswersThemWithAnError()
    {
        using var bare = new BlenderBridgeServer(action => action(), (_, _) => { })
        {
            Handlers = new BlenderBridgeHandlers(() => "m", () => new BridgeSnapshot(Array.Empty<BridgeMesh>(), Array.Empty<string>()),
                _ => "", _ => ""),
        };
        Assert.True(bare.Start(0, out _));
        using var client = new TcpClient();
        var stream = Connect(bare.Port, client);
        Send(stream, "{\"op\":\"hello\",\"protocol\":5}");
        foreach (string op in new[] { "materials", "texture", "assign_material", "edit_material" })
        {
            var (reply, _) = Send(stream, "{\"op\":\"" + op + "\",\"path\":\"x\"}");
            Assert.Equal("error", reply.GetProperty("op").GetString());
        }
    }

    // ---- M841 review: what a web page can and cannot do to the link ------------------------------

    /// <summary>True when the server closed the connection (a read returns end of stream).</summary>
    private static bool Closed(NetworkStream stream)
    {
        stream.ReadTimeout = 5000;
        try { return BlenderBridgeProtocol.ReadHeader(stream) is null; }
        catch (IOException) { return true; }
    }

    private const string Push = "{\"op\":\"push\",\"transforms\":[{\"i\":4,\"loc\":[9,9,9],\"rot\":[0,0,0],\"scl\":[1,1,1]}]}";
    private const string Assign = "{\"op\":\"assign_material\",\"assignments\":[{\"mesh\":4,\"material\":\"Rock_MAT\"}]}";

    [Fact]
    public void AnHttpPostFromAWebPageAppliesNothingEvenWithValidOpLines()
    {
        _server.Token = BlenderBridgeServer.NewToken();
        int port = Start();
        // what fetch("http://127.0.0.1:47800", {method: "POST", mode: "no-cors", body}) sends: a text/plain body
        // whose lines are valid link requests (even a valid hello - the page cannot know the token)
        string body = "{\"op\":\"hello\",\"protocol\":5}\n" + Push + "\n" + Assign + "\n";
        string http = "POST / HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Type: text/plain\r\nContent-Length: "
                      + System.Text.Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body;
        using (var client = new TcpClient())
        {
            var stream = Connect(port, client);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(http));
            Assert.True(Closed(stream));   // closed on the request line, with not one byte of answer
        }
        // the body alone, as a page could send it over a raw socket-like path: no token, so refused and closed
        using (var client = new TcpClient())
        {
            var stream = Connect(port, client);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(body));
            var refusal = JsonDocument.Parse(BlenderBridgeProtocol.ReadHeader(stream)!).RootElement;
            Assert.Equal("error", refusal.GetProperty("op").GetString());
            Assert.Contains("token", refusal.GetProperty("message").GetString());
            Assert.True(Closed(stream));
        }
        // a first line that is not a hello closes the connection too
        using (var client = new TcpClient())
        {
            var stream = Connect(port, client);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(Push + "\n" + Assign + "\n"));
            var reply = JsonDocument.Parse(BlenderBridgeProtocol.ReadHeader(stream)!).RootElement;
            Assert.Equal("error", reply.GetProperty("op").GetString());
            Assert.True(Closed(stream));
        }
        Assert.Equal(0, Pushed);
        Assert.Empty(Assigned);
    }

    [Fact]
    public void TheTokenIsRequiredAndForgivesCaseAndDashes()
    {
        _server.Token = "K7Q2-9XMB";
        int port = Start();
        using (var client = new TcpClient())
        {
            var stream = Connect(port, client);
            var (wrong, _) = Send(stream, "{\"op\":\"hello\",\"protocol\":5,\"token\":\"AAAA-BBBB\"}");
            Assert.Equal("error", wrong.GetProperty("op").GetString());
            Assert.Contains("wrong link token", wrong.GetProperty("message").GetString());
        }
        using (var client = new TcpClient())
        {
            var stream = Connect(port, client);
            var (hello, _) = Send(stream, "{\"op\":\"hello\",\"protocol\":5,\"token\":\"k7q29xmb\"}");
            Assert.Equal("hello", hello.GetProperty("op").GetString());
            var (ok, _) = Send(stream, Push);
            Assert.Equal("ok", ok.GetProperty("op").GetString());
        }
        Assert.Equal(1, Pushed);
        var token = BlenderBridgeServer.NewToken();
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", token);
        Assert.DoesNotContain('O', token.Replace("-", ""));
    }

    [Fact]
    public void AnOversizedBodyIsRefusedAndASilentClientDoesNotBlockTheLink()
    {
        int port = Start();
        using var silent = new TcpClient();
        silent.Connect(System.Net.IPAddress.Loopback, port);   // connects and says nothing

        using var client = new TcpClient();
        var stream = Connect(port, client);
        Send(stream, "{\"op\":\"hello\",\"protocol\":5}");
        var (pull, _) = Send(stream, "{\"op\":\"pull\"}");
        Assert.Equal("meshes", pull.GetProperty("op").GetString());   // served while the silent one waits

        var (big, _) = Send(stream, "{\"op\":\"push_geometry\",\"bytes\":" + (600L * 1024 * 1024) + "}");
        Assert.Equal("error", big.GetProperty("op").GetString());
        Assert.Contains("refused", big.GetProperty("message").GetString());
        Assert.True(Closed(stream));
    }

    [Fact]
    public void AHandlerTheServerGaveUpOnNeverRunsLate()
    {
        var queued = new List<Action>();
        int applied = 0;
        using var slow = new BlenderBridgeServer(a => { lock (queued) queued.Add(a); }, (_, _) => { })
        {
            HandlerTimeout = TimeSpan.FromMilliseconds(300),
            Handlers = new BlenderBridgeHandlers(() => "m", () => new BridgeSnapshot(Array.Empty<BridgeMesh>(), Array.Empty<string>()),
                t => { applied += t.Count; return "applied"; }, _ => ""),
        };
        Assert.True(slow.Start(0, out _));
        using var client = new TcpClient();
        var stream = Connect(slow.Port, client);
        BlenderBridgeProtocol.WriteMessage(stream, "{\"op\":\"hello\",\"protocol\":5}");
        // the hello itself waits on the editor thread for the map name: run it
        for (int i = 0; i < 50 && queued.Count == 0; i++) Thread.Sleep(20);
        lock (queued) { foreach (var a in queued) a(); queued.Clear(); }
        BlenderBridgeProtocol.ReadHeader(stream);

        var (late, _) = Send(stream, Push);   // nobody runs the editor's queue: times out
        Assert.Equal("error", late.GetProperty("op").GetString());
        Assert.Contains("nothing was applied", late.GetProperty("message").GetString());
        lock (queued) { foreach (var a in queued) a(); }   // the editor gets to it after all
        Assert.Equal(0, applied);
    }

    private static string ServerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReyEngine.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "ReyEngine.App", "Services", "BlenderBridgeServer.cs");
    }
}
