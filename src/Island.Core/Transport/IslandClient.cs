using System.IO.Pipes;
using System.Text.Json;

namespace Island.Core;

/// <summary>
/// Forwarder side of the pipe. Throws when the app is unreachable; callers treat that as "stay silent".
/// </summary>
public static class IslandClient
{
    /// <summary>
    /// Sends one hook payload. For PermissionRequest, waits for a decision until <paramref name="decisionTimeout"/>.
    /// </summary>
    /// <returns>The hookSpecificOutput JSON to print to stdout, or null when there is nothing to print.</returns>
    public static async Task<string?> ForwardHookAsync(
        string pipeName,
        string stdinJson,
        int clientPid,
        DateTimeOffset now,
        TimeSpan connectTimeout,
        TimeSpan decisionTimeout,
        string? clientApp = null)
    {
        using var doc = JsonDocument.Parse(stdinJson.TrimStart('﻿'));
        var root = doc.RootElement;
        var eventName = JsonUtil.Str(root, "hook_event_name");
        if (eventName is null) return null; // not a hook payload: nothing to forward

        var wait = eventName == "PermissionRequest";
        var id = Guid.NewGuid().ToString("N");
        var line = Frames.EncodeHook(id, now.ToUnixTimeMilliseconds(), clientPid, wait, root, clientApp);

        if (!PipeExists(pipeName)) throw new IOException("Island app is not running.");

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds).ConfigureAwait(false);
        await Frames.WriteLineAsync(pipe, line, CancellationToken.None).ConfigureAwait(false);
        if (!wait) return null;

        string? responseLine;
        using (var deadline = new CancellationTokenSource(decisionTimeout))
        {
            try
            {
                responseLine = await Frames.ReadLineAsync(pipe, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null; // deadline passed: fall back to Claude Code's own prompt
            }
        }
        if (responseLine is null) return null;

        var (responseId, decision) = Frames.DecodeResponse(responseLine);
        if (responseId != id || decision is null) return null;
        return PermissionDecision.HookOutput(decision);
    }

    /// <summary>
    /// When no server instance exists, ConnectAsync(timeout) keeps retrying until the full timeout (~200 ms per hook
    /// while the app is closed). Listing the pipe namespace costs about 3 ms, so check that first.
    /// </summary>
    private static bool PipeExists(string pipeName) =>
        Directory.EnumerateFileSystemEntries(@"\\.\pipe\")
            .Any(entry => string.Equals(Path.GetFileName(entry), pipeName, StringComparison.OrdinalIgnoreCase));
}
