using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using ReyEngine.Formats.Interop;

namespace ReyEngine.App.Services;

/// <summary>What the editor supplies to answer one request. All of them run on the UI thread.</summary>
/// <remarks>M841: the protocol 3 operations are init-only and optional - a host that leaves one null answers
/// that op with an error reply instead of a crash, which is what the socket tests rely on.</remarks>
public sealed record BlenderBridgeHandlers(
    Func<string> MapName,
    Func<BridgeSnapshot> Pull,
    Func<IReadOnlyList<BridgeTransform>, string> PushTransforms,
    Func<IReadOnlyList<BridgeMesh>, string> PushGeometry)
{
    /// <summary>M841: project and map information for the hello reply.</summary>
    public Func<Dictionary<string, object?>>? HelloInfo { get; init; }
    /// <summary>M841: the <c>materials</c> reply body (every field but <c>op</c>).</summary>
    public Func<Dictionary<string, object?>>? Materials { get; init; }
    /// <summary>M841: a texture by path or 0x link, from the editor's mounts (project first, then the game).</summary>
    public Func<string, BridgeTexture>? Texture { get; init; }
    /// <summary>M841: material assignments, applied as pending, undoable map edits.</summary>
    public Func<IReadOnlyList<BridgeAssignment>, BridgeEditReply>? AssignMaterial { get; init; }
    /// <summary>M841: material edits, applied through the material editor's own commands.</summary>
    public Func<IReadOnlyList<BridgeMaterialEdit>, BridgeEditReply>? EditMaterial { get; init; }
    /// <summary>M841: reshapes with a per-mesh answer (refusal index = position in the pushed block). Used instead
    /// of <see cref="PushGeometry"/> when set, so the add-on re-baselines only the meshes that were queued.</summary>
    public Func<IReadOnlyList<BridgeMesh>, BridgeEditReply>? PushGeometryEdits { get; init; }
    /// <summary>M842: the open map's navgrid and the editor's overlay layers (read-only: shown, never edited).</summary>
    public Func<BridgeNavGrid>? NavGrid { get; init; }
    /// <summary>M843: the same, for a request that may name a variant and a visibility selection (the whole request object).
    /// Used instead of <see cref="NavGrid"/> when set.</summary>
    public Func<JsonElement, BridgeNavGrid>? NavGridFor { get; init; }
    /// <summary>M843: the <c>navgrid_variants</c> reply body (every field but <c>op</c>).</summary>
    public Func<Dictionary<string, object?>>? NavGridVariants { get; init; }
    /// <summary>M843: the <c>visibility</c> reply body: the editor's visibility model and current selection.</summary>
    public Func<Dictionary<string, object?>>? Visibility { get; init; }
    /// <summary>M843: the <c>visibility_eval</c> reply body for the request's selection.</summary>
    public Func<JsonElement, Dictionary<string, object?>>? VisibilityEval { get; init; }
}

/// <summary>
/// M580: the editor half of the Blender link.
///
/// <para>ReyEngine listens and Blender connects, because the editor is the one holding the map — the link
/// should survive Blender being opened, closed and reopened, and it should not depend on a port some other
/// add-on may already own.</para>
///
/// <para>Bound to loopback only, and that is not a default to be relaxed: the link hands out a map's whole
/// geometry and accepts edits to it with no authentication of any kind.</para>
///
/// <para>M841: protocol 3, spoken by MapgeoAddon (which replaced the single-file "ReyEngine Bridge" add-on).
/// Every connection starts with <c>hello</c>; a protocol 2 client is told the old add-on was replaced. A
/// request that fails is answered with an <c>error</c> reply - never by dropping the connection, unless the
/// stream itself is no longer in step. The first line must be a hello carrying the session <see cref="Token"/>;
/// anything else (an HTTP request from a web page above all) closes the connection unread.</para>
/// </summary>
public sealed class BlenderBridgeServer : IDisposable
{
    private readonly Action<Action> _onUiThread;
    private readonly Action<string, string> _log;   // (level, message)
    private TcpListener? _listener;
    private Thread? _thread;
    private CancellationTokenSource? _cancel;
    private int _clients;
    private string? _announced;

    /// <summary>Connections served at once. Blender opens one per action; a few more cover a second Blender or a
    /// stale socket, and a client that never speaks cannot hold the link for anyone else.</summary>
    public const int MaxClients = 4;

    /// <summary>Ceiling on a request body (a pushed mesh block). A larger one is refused and the connection closed.</summary>
    public const int MaxBodyBytes = 512 * 1024 * 1024;

    /// <summary>Ceiling on the first line of a connection, which must be a hello.</summary>
    public const int MaxHelloBytes = 16 * 1024;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }
    public bool HasClient => Volatile.Read(ref _clients) > 0;

    /// <summary>The client named in the last accepted hello ("MapgeoAddon 0.5.0"); null when none is connected.</summary>
    public string? ClientName { get; private set; }

    /// <summary>
    /// M841: the session token every hello must carry. Generated when the link starts and shown in ReyEngine,
    /// so a web page that POSTs to 127.0.0.1 - which can reach a loopback port but cannot know the token - is
    /// turned away. Null or empty switches the check off (tests that are about something else).
    /// </summary>
    public string? Token { get; set; }

    /// <summary>How long a connection may sit silent: before its hello, and between requests after it.</summary>
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Raised after any change the editor needs to react to (re-render, undo entry).</summary>
    public event Action? MapChanged;

    public BlenderBridgeServer(Action<Action> onUiThread, Action<string, string> log)
    {
        _onUiThread = onUiThread ?? throw new ArgumentNullException(nameof(onUiThread));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public BlenderBridgeHandlers? Handlers { get; set; }

    /// <summary>How long one handler may hold the editor's thread before the request is answered with an error.</summary>
    public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private const string TokenAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O, 1/I: it is read off a screen

    /// <summary>A new session token, "XXXX-XXXX" (40 bits).</summary>
    public static string NewToken()
    {
        Span<char> chars = stackalloc char[9];
        for (int i = 0; i < 9; i++)
            chars[i] = i == 4 ? '-' : TokenAlphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(TokenAlphabet.Length)];
        return new string(chars);
    }

    /// <summary>Tokens compare without case, dashes or spaces - typed by hand, they should not fail on those.</summary>
    public static bool TokenMatches(string? expected, string? given)
    {
        static string Norm(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        string a = Norm(expected), b = Norm(given);
        return a.Length > 0 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    }

    public bool Start(int port, out string? error)
    {
        error = null;
        if (IsRunning) return true;
        if (Handlers is null) { error = "The bridge has no map to serve."; return false; }
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        catch (SocketException ex)
        {
            error = $"Could not listen on 127.0.0.1:{port} — {ex.SocketErrorCode}. "
                  + "Another ReyEngine may already have the link open.";
            _listener = null;
            return false;
        }

        _cancel = new CancellationTokenSource();
        IsRunning = true;
        _thread = new Thread(() => Accept(_cancel.Token)) { IsBackground = true, Name = "blender-bridge" };
        _thread.Start();
        _log("info", $"Blender link listening on 127.0.0.1:{Port}. Connect from the ReyEngine panel in MapgeoAddon (LoL Mapgeo tab).");
        return true;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        ClientName = null;
        try { _cancel?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _log("info", "Blender link stopped.");
    }

    public void Dispose() => Stop();

    private void Accept(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is { } listener)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { break; }   // listener stopped

            if (Interlocked.Increment(ref _clients) > MaxClients)
            {
                Interlocked.Decrement(ref _clients);
                try { client.Dispose(); } catch { }
                continue;
            }
            // One thread per connection: a client that connects and says nothing must not hold the link.
            var worker = new Thread(() =>
            {
                try { Serve(client, token); }
                catch (Exception ex) when (ex is System.IO.IOException or ObjectDisposedException or SocketException
                                               or System.IO.InvalidDataException or System.IO.EndOfStreamException) { }
                catch (Exception ex) { _log("warn", $"Blender link: {ex.Message}"); }
                finally
                {
                    try { client.Dispose(); } catch { }
                    if (Interlocked.Decrement(ref _clients) == 0) ClientName = null;
                }
            }) { IsBackground = true, Name = "blender-bridge-client" };
            worker.Start();
        }
    }

    /// <summary>Is this the first line of something other than the link - an HTTP request a web page sent?</summary>
    private static bool LooksLikeHttp(string line)
    {
        foreach (string verb in new[] { "GET", "POST", "PUT", "DELETE", "HEAD", "OPTIONS", "PATCH", "CONNECT", "TRACE" })
            if (line.StartsWith(verb + " ", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void Serve(TcpClient client, CancellationToken token)
    {
        client.NoDelay = true;
        client.ReceiveTimeout = (int)HelloTimeout.TotalMilliseconds;
        using var stream = client.GetStream();

        // ---- the hello. Anything else on the first line closes the connection without another word: a
        // browser's POST to this port carries its body lines after HTTP headers, and none of them may ever be
        // read as an op.
        string? first = BlenderBridgeProtocol.ReadHeader(stream, MaxHelloBytes);
        if (first is null || first.Length == 0 || LooksLikeHttp(first) || !first.TrimStart().StartsWith('{')) return;
        JsonElement hello;
        try { hello = JsonDocument.Parse(first).RootElement.Clone(); }
        catch (JsonException) { return; }
        if (hello.ValueKind != JsonValueKind.Object
            || !hello.TryGetProperty("op", out var helloOp) || helloOp.ValueKind != JsonValueKind.String || helloOp.GetString() != "hello")
        {
            Fail(stream, "say hello first: every connection starts with {\"op\":\"hello\",\"protocol\":"
                         + BlenderBridgeProtocol.Version + ",\"token\":\"...\"}");
            CloseAfterReply(client, stream);
            return;
        }
        if (!Hello(stream, hello)) { CloseAfterReply(client, stream); return; }   // refused: said why, and the connection ends

        client.ReceiveTimeout = (int)IdleTimeout.TotalMilliseconds;
        while (!token.IsCancellationRequested)
        {
            string? header = BlenderBridgeProtocol.ReadHeader(stream);
            if (header is null) return;
            if (header.Length == 0) continue;

            JsonElement request;
            try { request = JsonDocument.Parse(header).RootElement.Clone(); }
            catch (JsonException ex) { Fail(stream, $"unreadable request: {ex.Message}"); continue; }
            if (request.ValueKind != JsonValueKind.Object) { Fail(stream, "a request is a JSON object"); continue; }

            string op = request.TryGetProperty("op", out var opValue) && opValue.ValueKind == JsonValueKind.String
                ? opValue.GetString() ?? "" : "";
            long bodyBytes = request.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number
                && b.TryGetInt64(out long n) ? n : 0;

            try
            {
                if (bodyBytes < 0 || bodyBytes > MaxBodyBytes)
                    throw new BodyTooLargeException($"a body of {bodyBytes:n0} bytes is refused (the limit is {MaxBodyBytes:n0})");

                // A body that the op does not read is drained, so the next header starts where it should.
                if (op != "push_geometry" && bodyBytes > 0) BlenderBridgeProtocol.Skip(stream, bodyBytes);

                switch (op)
                {
                    case "hello": Fail(stream, "this connection already said hello"); break;
                    case "pull": PullMeshes(stream); break;
                    case "materials": SendMaterials(stream); break;
                    case "texture": SendTexture(stream, request); break;
                    case "push": PushTransforms(stream, request); break;
                    case "push_geometry": PushGeometry(stream, (int)bodyBytes); break;
                    case "assign_material": AssignMaterial(stream, request); break;
                    case "edit_material": EditMaterial(stream, request); break;
                    case "navgrid": SendNavGrid(stream, request); break;
                    case "navgrid_variants": SendSimple(stream, "navgrid_variants", Handlers?.NavGridVariants, "the navgrid variants"); break;
                    case "visibility": SendSimple(stream, "visibility", Handlers?.Visibility, "the map visibility"); break;
                    case "visibility_eval": SendVisibilityEval(stream, request); break;
                    default: Fail(stream, $"unknown op '{op}'"); break;
                }
            }
            catch (BodyTooLargeException ex)
            {
                // the body cannot be skipped safely, so the stream cannot be kept in step: say so and close
                Fail(stream, ex.Message);
                CloseAfterReply(client, stream);
                return;
            }
            catch (Exception ex) when (ex is not System.IO.IOException and not ObjectDisposedException
                                           and not System.IO.EndOfStreamException)
            {
                // The editor threw or timed out. The stream is still in step (bodies are read before the
                // handler runs), so the client gets an answer and the connection stays usable.
                Fail(stream, $"{op} failed in ReyEngine: {ex.Message}");
            }
        }
    }

    /// <summary>End a connection after a refusal so the refusal ARRIVES: closing with unread request bytes in the
    /// socket resets it, and the client then sees "connection reset" instead of the reason. Sends are shut, what
    /// is still coming is read and dropped (briefly, and never more than a little), then the socket closes.</summary>
    private static void CloseAfterReply(TcpClient client, System.IO.Stream stream)
    {
        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
            client.ReceiveTimeout = 500;
            var sink = new byte[4096];
            int total = 0;
            while (total < 256 * 1024)
            {
                int n = stream.Read(sink, 0, sink.Length);
                if (n <= 0) break;
                total += n;
            }
        }
        catch { /* gone already, or timed out: either way the connection ends */ }
    }

    private sealed class BodyTooLargeException(string message) : Exception(message);

    // ---- operations ------------------------------------------------------------------------------

    private bool Hello(System.IO.Stream stream, JsonElement request)
    {
        int theirs = request.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.Number
            && p.TryGetInt32(out int v) ? v : 0;
        string client = request.TryGetProperty("client", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? "" : "";
        if (theirs != BlenderBridgeProtocol.Version)
        {
            // Refused rather than attempted. A mismatched decode does not fail loudly - it produces
            // geometry that looks like corruption.
            Fail(stream, theirs <= BlenderBridgeProtocol.LegacyBridgeVersion
                ? $"this ReyEngine speaks Blender link protocol {BlenderBridgeProtocol.Version} and works with MapgeoAddon. "
                  + "The old 'ReyEngine Bridge' add-on (reyengine_bridge.py, protocol 2) has been replaced: disable it in "
                  + "Blender and install MapgeoAddon from ReyEngine (Settings > Blender)."
                : theirs < BlenderBridgeProtocol.Version
                    ? $"this MapgeoAddon speaks link protocol {theirs}, this ReyEngine speaks {BlenderBridgeProtocol.Version}. Update MapgeoAddon (ReyEngine > Settings > Blender)."
                    : $"this MapgeoAddon speaks link protocol {theirs}, this ReyEngine speaks {BlenderBridgeProtocol.Version}. Update ReyEngine.");
            return false;
        }
        if (!string.IsNullOrEmpty(Token))
        {
            string given = request.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            if (!TokenMatches(Token, given))
            {
                Fail(stream, given.Length == 0
                    ? "ReyEngine asks for the link token: copy it from ReyEngine (Tools menu, under Blender link) into the ReyEngine panel's Token field."
                    : "wrong link token - ReyEngine makes a new one each time the link starts; copy the current one from ReyEngine (Tools menu).");
                return false;
            }
        }

        var reply = new Dictionary<string, object?>
        {
            ["op"] = "hello",
            ["protocol"] = BlenderBridgeProtocol.Version,
            ["app"] = "ReyEngine",
            ["app_version"] = typeof(BlenderBridgeServer).Assembly.GetName().Version?.ToString(3),
            ["map"] = "",
        };
        try
        {
            reply["map"] = Run(() => Handlers!.MapName(), "");
            if (Handlers?.HelloInfo is { } info)
                foreach (var (key, value) in Run(info, new Dictionary<string, object?>())) reply[key] = value;
        }
        catch (Exception ex) { reply["warning"] = "the editor could not describe the map: " + ex.Message; }

        ClientName = client.Length > 0 ? client : "an unnamed client";
        if (_announced != ClientName) _log("success", $"Blender connected ({ClientName}).");   // once per client, not per action
        _announced = ClientName;
        Respond(stream, reply);
        return true;
    }

    private void PullMeshes(System.IO.Stream stream)
    {
        var snapshot = Run(() => Handlers!.Pull(), new BridgeSnapshot(Array.Empty<BridgeMesh>(), new[] { "no map is open" }));
        byte[] body = BlenderBridgeProtocol.EncodeMeshes(snapshot.Meshes);
        var header = new Dictionary<string, object?>
        {
            ["op"] = "meshes",
            ["protocol"] = BlenderBridgeProtocol.Version,
            ["count"] = snapshot.Meshes.Count,
            ["bytes"] = body.Length,
            ["notes"] = snapshot.Notes,
            ["meshes"] = snapshot.MeshInfo,
        };
        foreach (var (key, value) in snapshot.MapInfo) header.TryAdd(key, value);
        BlenderBridgeProtocol.WriteMessage(stream, JsonSerializer.Serialize(header, Json), body);
        _log("info", $"Sent {snapshot.Meshes.Count:n0} mesh(es) to Blender ({body.Length / 1024:n0} KB).");
        foreach (string note in snapshot.Notes) _log("warn", note);
    }

    private void SendMaterials(System.IO.Stream stream)
    {
        if (Handlers?.Materials is not { } materials) { Fail(stream, "this ReyEngine cannot describe materials"); return; }
        var body = Run(materials, new Dictionary<string, object?>());
        var reply = new Dictionary<string, object?> { ["op"] = "materials", ["protocol"] = BlenderBridgeProtocol.Version };
        foreach (var (key, value) in body) reply[key] = value;
        Respond(stream, reply);
    }

    private void SendTexture(System.IO.Stream stream, JsonElement request)
    {
        if (Handlers?.Texture is not { } texture) { Fail(stream, "this ReyEngine cannot serve textures"); return; }
        string path = request.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        if (path.Length == 0 && request.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.String) path = l.GetString() ?? "";
        if (path.Length == 0) { Fail(stream, "texture needs a path or a link"); return; }

        var answer = Run(() => texture(path), new BridgeTexture(false, path, 0, "", "", null, "no map is open"));
        byte[] body = answer.Found && answer.Bytes is { } bytes ? bytes : Array.Empty<byte>();
        var header = new Dictionary<string, object?>
        {
            ["op"] = "texture",
            ["found"] = answer.Found && body.Length > 0,
            ["request"] = path,
            ["path"] = answer.Path,
            ["link"] = answer.Link == 0 ? "" : $"0x{answer.Link:x16}",
            ["ext"] = answer.Extension,
            ["source"] = answer.Source,
            ["bytes"] = body.Length,
        };
        if (answer.Message is { Length: > 0 } message) header["message"] = message;
        BlenderBridgeProtocol.WriteMessage(stream, JsonSerializer.Serialize(header, Json), body);
    }

    private void PushTransforms(System.IO.Stream stream, JsonElement request)
    {
        var transforms = new List<BridgeTransform>();
        if (request.TryGetProperty("transforms", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("i", out var indexValue) || !indexValue.TryGetInt32(out int index)) continue;
                // "anc" is the pivot the add-on was given. Absent means an older add-on, and the
                // handler falls back to the mesh's current pivot.
                Vector3? anchor = item.TryGetProperty("anc", out _) ? Vec(item, "anc", Vector3.Zero) : null;
                transforms.Add(new BridgeTransform(index,
                    Vec(item, "loc", Vector3.Zero), Vec(item, "rot", Vector3.Zero), Vec(item, "scl", Vector3.One),
                    anchor));
            }
        }
        if (transforms.Count == 0) { Respond(stream, new Dictionary<string, object?> { ["op"] = "ok", ["applied"] = 0 }); return; }

        string summary = Run(() => Handlers!.PushTransforms(transforms), "no map is open");
        MapChanged?.Invoke();
        Respond(stream, new Dictionary<string, object?> { ["op"] = "ok", ["applied"] = transforms.Count, ["detail"] = summary });
    }

    /// <summary>
    /// Reshaped meshes coming back. The body is the same mesh block a pull sends, so the two directions
    /// cannot drift apart; the placement fields it carries are ignored here because a shape push and a
    /// placement push are separate operations.
    /// </summary>
    private void PushGeometry(System.IO.Stream stream, int bytes)
    {
        if (bytes <= 0) { Fail(stream, "push_geometry with no body"); return; }
        byte[] payload = BlenderBridgeProtocol.ReadExactly(stream, bytes);

        IReadOnlyList<BridgeMesh> meshes;
        try { meshes = BlenderBridgeProtocol.DecodeMeshes(payload); }
        catch (Exception ex) { Fail(stream, "unreadable geometry: " + ex.Message); return; }

        if (Handlers?.PushGeometryEdits is { } detailed)
        {
            var reply = Run(() => detailed(meshes), new BridgeEditReply(0, Array.Empty<BridgeRefusal>(), "no map is open"));
            if (reply.Applied > 0) MapChanged?.Invoke();
            RespondEdits(stream, reply, 0);
            return;
        }
        string summary = Run(() => Handlers!.PushGeometry(meshes), "no map is open");
        MapChanged?.Invoke();
        Respond(stream, new Dictionary<string, object?> { ["op"] = "ok", ["applied"] = meshes.Count, ["detail"] = summary });
    }

    private void AssignMaterial(System.IO.Stream stream, JsonElement request)
    {
        if (Handlers?.AssignMaterial is not { } assign) { Fail(stream, "this ReyEngine cannot assign materials"); return; }
        var assignments = BlenderLinkRequests.Assignments(request, out int unreadable);
        if (assignments.Count == 0)
        { Fail(stream, unreadable > 0 ? $"none of the {unreadable} assignment(s) could be read" : "assign_material with no assignments"); return; }

        var reply = Run(() => assign(assignments), new BridgeEditReply(0, Array.Empty<BridgeRefusal>(), "no map is open"));
        if (reply.Applied > 0) MapChanged?.Invoke();
        RespondEdits(stream, reply, unreadable);
    }

    private void EditMaterial(System.IO.Stream stream, JsonElement request)
    {
        if (Handlers?.EditMaterial is not { } edit) { Fail(stream, "this ReyEngine cannot edit materials"); return; }
        var edits = BlenderLinkRequests.MaterialEdits(request, out int unreadable);
        if (edits.Count == 0)
        { Fail(stream, unreadable > 0 ? $"none of the {unreadable} edit(s) could be read" : "edit_material with no edits"); return; }

        var reply = Run(() => edit(edits), new BridgeEditReply(0, Array.Empty<BridgeRefusal>(), "no map is open"));
        if (reply.Applied > 0) MapChanged?.Invoke();
        RespondEdits(stream, reply, unreadable);
    }

    /// <summary>M843: a read-only op whose whole reply is a dictionary the editor builds.</summary>
    private void SendSimple(System.IO.Stream stream, string op, Func<Dictionary<string, object?>>? work, string what)
    {
        if (work is null) { Fail(stream, $"this ReyEngine cannot serve {what}"); return; }
        var body = Run(work, new Dictionary<string, object?> { ["found"] = false, ["message"] = "no map is open" });
        var reply = new Dictionary<string, object?>(body) { ["op"] = op, ["bytes"] = 0 };
        BlenderBridgeProtocol.WriteMessage(stream, JsonSerializer.Serialize(reply, Json));
    }

    private void SendVisibilityEval(System.IO.Stream stream, JsonElement request)
    {
        if (Handlers?.VisibilityEval is not { } eval) { Fail(stream, "this ReyEngine cannot evaluate map visibility"); return; }
        var body = Run(() => eval(request), new Dictionary<string, object?> { ["found"] = false, ["message"] = "no map is open" });
        var reply = new Dictionary<string, object?>(body) { ["op"] = "visibility_eval", ["bytes"] = 0 };
        BlenderBridgeProtocol.WriteMessage(stream, JsonSerializer.Serialize(reply, Json));
    }

    private void SendNavGrid(System.IO.Stream stream, JsonElement request)
    {
        if (Handlers?.NavGrid is null && Handlers?.NavGridFor is null) { Fail(stream, "this ReyEngine cannot serve the navgrid"); return; }
        var fallback = new BridgeNavGrid(false, "", "", null, Array.Empty<BridgeNavLayer>(), "no map is open");
        var answer = Handlers!.NavGridFor is { } forRequest ? Run(() => forRequest(request), fallback) : Run(Handlers.NavGrid!, fallback);
        var header = new Dictionary<string, object?>
        {
            ["op"] = "navgrid",
            ["found"] = answer.Found && answer.Grid is not null,
            ["path"] = answer.Path,
            ["source"] = answer.Source,
            ["bytes"] = 0,
        };
        if (answer.Message is { Length: > 0 } message) header["message"] = message;
        if (answer.Variant.Length > 0)   // M843
        {
            header["variant"] = answer.Variant;
            header["base"] = answer.BaseId;
            header["overlays"] = answer.Overlays.Select(o => new Dictionary<string, object?>
            { ["id"] = o.Id, ["label"] = o.Label, ["path"] = o.Path, ["changed"] = o.Changed, ["outside"] = o.Outside }).ToList();
        }
        byte[] body = Array.Empty<byte>();
        if (answer.Found && answer.Grid is { } grid)
        {
            body = BlenderLinkNavGrid.Encode(grid);
            header["version"] = $"{grid.VersionMajor}.{grid.VersionMinor}";
            header["min"] = new[] { grid.Min.X, grid.Min.Y, grid.Min.Z };
            header["max"] = new[] { grid.Max.X, grid.Max.Y, grid.Max.Z };
            header["cell_size"] = grid.CellSize;
            header["count_x"] = grid.CountX;
            header["count_z"] = grid.CountZ;
            header["has_heights"] = grid.HasHeights;
            header["lift"] = BridgeNavGrid.Lift;
            // the editor's overlay layers, so Blender never hard-codes names, colours or the default
            header["layers"] = answer.Layers.Select(l => new Dictionary<string, object?>
            {
                ["mask"] = (int)l.Mask,
                ["label"] = l.Label,
                ["color"] = new[] { l.Color.X, l.Color.Y, l.Color.Z, l.Color.W },
                ["cells"] = l.Cells,
                ["visible"] = l.VisibleInEditor,
                ["default_visible"] = l.DefaultVisible,
            }).ToList();
            header["bytes"] = body.Length;
        }
        BlenderBridgeProtocol.WriteMessage(stream, JsonSerializer.Serialize(header, Json), body);
    }

    private void RespondEdits(System.IO.Stream stream, BridgeEditReply reply, int unreadable)
    {
        var refused = reply.Refused.Select(r => new Dictionary<string, object?>
        {
            ["index"] = r.Index, ["what"] = r.What, ["reason"] = r.Reason,
        }).ToList();
        Respond(stream, new Dictionary<string, object?>
        {
            ["op"] = "ok",
            ["applied"] = reply.Applied,
            ["refused"] = refused,
            ["unreadable"] = unreadable,
            ["detail"] = reply.Detail,
        });
        foreach (var r in reply.Refused.Take(6)) _log("warn", $"Blender: {r.What}: {r.Reason}");
    }

    private static Vector3 Vec(JsonElement item, string name, Vector3 fallback)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return fallback;
        var parts = value.EnumerateArray().ToList();
        if (parts.Count != 3 || parts.Any(x => x.ValueKind != JsonValueKind.Number)) return fallback;
        return new Vector3(
            (float)parts[0].GetDouble(), (float)parts[1].GetDouble(), (float)parts[2].GetDouble());
    }

    // ---- plumbing --------------------------------------------------------------------------------

    /// <summary>Run a handler on the UI thread and wait for it. The map is not thread-safe, and every
    /// handler here either reads geometry the renderer is using or writes geometry it is about to.</summary>
    private T Run<T>(Func<T> work, T fallback)
    {
        if (Handlers is null) return fallback;
        T result = fallback;
        // Not disposed: a posted job that starts after the wait gave up still sets it, and setting a disposed
        // event throws on the editor's thread. It holds no unmanaged handle until something waits on it.
        var done = new ManualResetEventSlim(false);
        Exception? failure = null;
        int state = 0;   // 0 queued, 1 running, 2 abandoned
        _onUiThread(() =>
        {
            // A job the waiting side gave up on must not run late: its reply was already an error, the client may
            // resend it, and applying it now would apply it twice.
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(HandlerTimeout))
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                throw new TimeoutException($"the editor did not answer within {HandlerTimeout.TotalSeconds:0}s; nothing was applied");
            done.Wait();   // it started just now: it will finish, and its answer is the true one
        }
        if (failure is not null) throw failure;
        return result;
    }

    /// <summary>NaN and infinity are written as Python's json reads them, instead of throwing in the middle of
    /// a reply: a material parameter can hold either.</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static void Respond(System.IO.Stream stream, Dictionary<string, object?> payload)
        => BlenderBridgeProtocol.WriteMessage(stream, JsonSerializer.Serialize(payload, Json));

    private void Fail(System.IO.Stream stream, string message)
    {
        _log("warn", "Blender link: " + message);
        Respond(stream, new Dictionary<string, object?> { ["op"] = "error", ["message"] = message });
    }
}
