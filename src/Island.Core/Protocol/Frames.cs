using System.Text;
using System.Text.Json;

namespace Island.Core;

/// <summary>
/// One request from a forwarder (hook invocation) to the island app.
/// </summary>
/// <param name="Agent">Id of the agent integration that understands the payload ("claude-code").</param>
/// <param name="Wait">True when the forwarder is blocked waiting for a decision (a permission request).</param>
/// <param name="PayloadJson">The hook's stdin JSON, compact. Only the agent integration reads it.</param>
/// <param name="ClientApp">Friendly name of the app running the agent ("VS Code", "Terminal"), when known.</param>
/// <param name="Project">The folder the agent was started in, when the forwarder knows it; else the payload's cwd is used.</param>
public sealed record HookFrame(
    string Id, string Agent, long ReceivedAtUnixMs, int ClientPid, bool Wait, string PayloadJson, string? ClientApp = null, string? Project = null);

/// <summary>
/// Wire format between forwarder and app: newline-delimited UTF-8 JSON over a named pipe.
/// <code>
/// forwarder → app: {"v":2,"id":"…","kind":"hook","agent":"claude-code","receivedAt":ms,"clientPid":n,"wait":bool,
///                   "clientApp":"…"?,"project":"…"?,"payload":{…}}
/// app → forwarder: {"v":2,"id":"…","output":"…" | null}     (only when wait=true; output is printed to stdout verbatim)
/// </code>
/// </summary>
public static class Frames
{
    public const int Version = 2;
    public const string HookKind = "hook";

    public static string EncodeHook(
        string id, string agent, long receivedAtUnixMs, int clientPid, bool wait, JsonElement payload, string? clientApp = null, string? project = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("id", id);
            w.WriteString("kind", HookKind);
            w.WriteString("agent", agent);
            w.WriteNumber("receivedAt", receivedAtUnixMs);
            w.WriteNumber("clientPid", clientPid);
            w.WriteBoolean("wait", wait);
            if (!string.IsNullOrEmpty(clientApp)) w.WriteString("clientApp", clientApp);
            if (!string.IsNullOrEmpty(project)) w.WriteString("project", project);
            w.WritePropertyName("payload");
            payload.WriteTo(w); // writes compact JSON, so the frame stays on one line
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static HookFrame DecodeHook(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (JsonUtil.Int32(root, "v", -1) != Version) throw new InvalidDataException("Unsupported frame version.");
        if (JsonUtil.Str(root, "kind") != HookKind) throw new InvalidDataException("Unsupported frame kind.");

        return new HookFrame(
            Id: JsonUtil.Str(root, "id") ?? throw new InvalidDataException("Frame has no id."),
            Agent: JsonUtil.Str(root, "agent") ?? throw new InvalidDataException("Frame names no agent."),
            ReceivedAtUnixMs: JsonUtil.Int64(root, "receivedAt"),
            ClientPid: JsonUtil.Int32(root, "clientPid"),
            Wait: JsonUtil.Bool(root, "wait"),
            PayloadJson: JsonUtil.Raw(root, "payload") ?? throw new InvalidDataException("Frame has no payload."),
            ClientApp: JsonUtil.Str(root, "clientApp"),
            Project: JsonUtil.Str(root, "project"));
    }

    /// <param name="output">Text for the forwarder to print to stdout, or null to print nothing.</param>
    public static string EncodeResponse(string id, string? output)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("id", id);
            if (output is null) w.WriteNull("output");
            else w.WriteString("output", output); // escaped, so the response stays on one line
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static (string Id, string? Output) DecodeResponse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var id = JsonUtil.Str(root, "id") ?? throw new InvalidDataException("Response has no id.");
        return (id, JsonUtil.Str(root, "output"));
    }

    /// <summary>Reads one newline-terminated frame. Returns null on end-of-stream before a complete line.</summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        using var accumulated = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false);
            if (read == 0) return null;

            var newline = Array.IndexOf(chunk, (byte)'\n', 0, read);
            var take = newline >= 0 ? newline : read;
            if (accumulated.Length + take > IslandPipe.MaxLineBytes)
                throw new InvalidDataException("Frame exceeds the size limit.");
            accumulated.Write(chunk, 0, take);

            if (newline >= 0)
                return Encoding.UTF8.GetString(accumulated.GetBuffer(), 0, (int)accumulated.Length).TrimEnd('\r');
        }
    }

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
    }
}
