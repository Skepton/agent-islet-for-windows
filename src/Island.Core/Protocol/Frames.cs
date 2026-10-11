using System.Text;
using System.Text.Json;

namespace Island.Core;

/// <summary>
/// One request from a forwarder (hook invocation) to the island app.
/// </summary>
/// <param name="Wait">True when the forwarder is blocked waiting for a decision (PermissionRequest).</param>
/// <param name="PayloadJson">The hook's stdin JSON, compact.</param>
/// <param name="ClientApp">Friendly name of the app running Claude Code ("VS Code", "Terminal"), when known.</param>
public sealed record HookFrame(string Id, long ReceivedAtUnixMs, int ClientPid, bool Wait, string PayloadJson, string? ClientApp = null);

/// <summary>Maps CLAUDE_CODE_ENTRYPOINT (set in the hook's environment) to a name for the card's chip.</summary>
public static class ClientAppNames
{
    public static string? FromEntrypoint(string? entrypoint) => entrypoint switch
    {
        "claude-vscode" => "VS Code",
        "cli" => "Terminal",
        "claude-desktop" => "Desktop",
        _ => null,
    };
}

/// <summary>
/// Wire format between forwarder and app: newline-delimited UTF-8 JSON over a named pipe.
/// <code>
/// forwarder → app: {"v":1,"id":"…","kind":"hook","receivedAt":ms,"clientPid":n,"wait":bool,"payload":{…}}
/// app → forwarder: {"v":1,"id":"…","decision":{…} | null}     (only when wait=true)
/// </code>
/// </summary>
public static class Frames
{
    public const int Version = 1;
    public const string HookKind = "hook";

    public static string EncodeHook(string id, long receivedAtUnixMs, int clientPid, bool wait, JsonElement payload, string? clientApp = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("id", id);
            w.WriteString("kind", HookKind);
            w.WriteNumber("receivedAt", receivedAtUnixMs);
            w.WriteNumber("clientPid", clientPid);
            w.WriteBoolean("wait", wait);
            if (!string.IsNullOrEmpty(clientApp)) w.WriteString("clientApp", clientApp);
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
            ReceivedAtUnixMs: JsonUtil.Int64(root, "receivedAt"),
            ClientPid: JsonUtil.Int32(root, "clientPid"),
            Wait: JsonUtil.Bool(root, "wait"),
            PayloadJson: JsonUtil.Raw(root, "payload") ?? throw new InvalidDataException("Frame has no payload."),
            ClientApp: JsonUtil.Str(root, "clientApp"));
    }

    public static string EncodeResponse(string id, string? decisionJson)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("id", id);
            w.WritePropertyName("decision");
            if (decisionJson is null) w.WriteNullValue();
            else w.WriteRawValue(decisionJson);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static (string Id, string? DecisionJson) DecodeResponse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var id = JsonUtil.Str(root, "id") ?? throw new InvalidDataException("Response has no id.");
        return (id, JsonUtil.Raw(root, "decision"));
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
