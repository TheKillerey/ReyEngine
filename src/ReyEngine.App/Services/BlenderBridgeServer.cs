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

/// <summary>What the editor supplies to answer one request. Both run on the UI thread.</summary>
public sealed record BlenderBridgeHandlers(
    Func<string> MapName,
    Func<BridgeSnapshot> Pull,
    Func<IReadOnlyList<BridgeTransform>, string> PushTransforms,
    Func<IReadOnlyList<BridgeMesh>, string> PushGeometry);

/// <summary>
/// M580: the editor half of the Blender link.
///
/// <para>ReyEngine listens and Blender connects, because the editor is the one holding the map — the link
/// should survive Blender being opened, closed and reopened, and it should not depend on a port some other
/// add-on may already own.</para>
///
/// <para>Bound to loopback only, and that is not a default to be relaxed: the link hands out a map's whole
/// geometry and accepts edits to it with no authentication of any kind.</para>
/// </summary>
public sealed class BlenderBridgeServer : IDisposable
{
    private readonly Action<Action> _onUiThread;
    private readonly Action<string, string> _log;   // (level, message)
    private TcpListener? _listener;
    private Thread? _thread;
    private CancellationTokenSource? _cancel;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }
    public bool HasClient { get; private set; }

    /// <summary>Raised after any change the editor needs to react to (re-render, undo entry).</summary>
    public event Action? MapChanged;

    public BlenderBridgeServer(Action<Action> onUiThread, Action<string, string> log)
    {
        _onUiThread = onUiThread ?? throw new ArgumentNullException(nameof(onUiThread));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public BlenderBridgeHandlers? Handlers { get; set; }

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
        _log("info", $"Blender link listening on 127.0.0.1:{Port}. Connect from the ReyEngine panel in Blender.");
        return true;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        HasClient = false;
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

            HasClient = true;
            _log("success", "Blender connected.");
            try { Serve(client, token); }
            catch (Exception ex) { _log("warn", $"Blender link: {ex.Message}"); }
            finally
            {
                HasClient = false;
                try { client.Dispose(); } catch { }
                if (!token.IsCancellationRequested) _log("info", "Blender disconnected.");
            }
        }
    }

    private void Serve(TcpClient client, CancellationToken token)
    {
        client.NoDelay = true;
        using var stream = client.GetStream();
        while (!token.IsCancellationRequested)
        {
            string? header = BlenderBridgeProtocol.ReadHeader(stream);
            if (header is null) return;
            if (header.Length == 0) continue;

            JsonElement request;
            try { request = JsonDocument.Parse(header).RootElement; }
            catch (JsonException ex) { Fail(stream, $"unreadable request: {ex.Message}"); continue; }

            string op = request.TryGetProperty("op", out var opValue) ? opValue.GetString() ?? "" : "";
            switch (op)
            {
                case "hello": Hello(stream, request); break;
                case "pull": PullMeshes(stream); break;
                case "push": PushTransforms(stream, request); break;
                case "push_geometry": PushGeometry(stream, request); break;
                default: Fail(stream, $"unknown op '{op}'"); break;
            }
        }
    }

    // ---- operations ------------------------------------------------------------------------------

    private void Hello(System.IO.Stream stream, JsonElement request)
    {
        int theirs = request.TryGetProperty("protocol", out var p) && p.TryGetInt32(out int v) ? v : 0;
        if (theirs != BlenderBridgeProtocol.Version)
        {
            // Refused rather than attempted. A mismatched decode does not fail loudly - it produces
            // geometry that looks like corruption, and the add-on is the easier half to update.
            Fail(stream, $"add-on speaks protocol {theirs}, this ReyEngine speaks "
                       + $"{BlenderBridgeProtocol.Version}. Update whichever is older.");
            return;
        }
        string map = Run(() => Handlers!.MapName(), "");
        Respond(stream, new Dictionary<string, object?>
        {
            ["op"] = "hello",
            ["protocol"] = BlenderBridgeProtocol.Version,
            ["app"] = "ReyEngine",
            ["map"] = map,
        });
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
        };
        BlenderBridgeProtocol.WriteMessage(stream, JsonSerializer.Serialize(header), body);
        _log("info", $"Sent {snapshot.Meshes.Count:n0} mesh(es) to Blender ({body.Length / 1024:n0} KB).");
        foreach (string note in snapshot.Notes) _log("warn", note);
    }

    private void PushTransforms(System.IO.Stream stream, JsonElement request)
    {
        var transforms = new List<BridgeTransform>();
        if (request.TryGetProperty("transforms", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
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
    private void PushGeometry(System.IO.Stream stream, JsonElement request)
    {
        int bytes = request.TryGetProperty("bytes", out var b) && b.TryGetInt32(out int n) ? n : 0;
        if (bytes <= 0) { Fail(stream, "push_geometry with no body"); return; }

        IReadOnlyList<BridgeMesh> meshes;
        try { meshes = BlenderBridgeProtocol.DecodeMeshes(BlenderBridgeProtocol.ReadExactly(stream, bytes)); }
        catch (Exception ex) { Fail(stream, "unreadable geometry: " + ex.Message); return; }

        string summary = Run(() => Handlers!.PushGeometry(meshes), "no map is open");
        MapChanged?.Invoke();
        Respond(stream, new Dictionary<string, object?> { ["op"] = "ok", ["applied"] = meshes.Count, ["detail"] = summary });
    }

    private static Vector3 Vec(JsonElement item, string name, Vector3 fallback)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return fallback;
        var parts = value.EnumerateArray().ToList();
        if (parts.Count != 3) return fallback;
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
        using var done = new ManualResetEventSlim(false);
        Exception? failure = null;
        _onUiThread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("the editor did not answer within 30s");
        if (failure is not null) throw failure;
        return result;
    }

    private static void Respond(System.IO.Stream stream, Dictionary<string, object?> payload)
        => BlenderBridgeProtocol.WriteMessage(stream, JsonSerializer.Serialize(payload));

    private void Fail(System.IO.Stream stream, string message)
    {
        _log("warn", "Blender link: " + message);
        Respond(stream, new Dictionary<string, object?> { ["op"] = "error", ["message"] = message });
    }
}
