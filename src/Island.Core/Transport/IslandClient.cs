using System.IO.Pipes;
using System.Text.Json;

namespace Island.Core;

/// <summary>
/// Forwarder side of the pipe, for any agent. Throws when the app is unreachable; callers treat that as "stay silent".
/// The agent integration decides which events wait for an answer and what the payload looks like.
/// </summary>
public static class IslandClient
{
    /// <summary>
    /// Sends one hook payload. When <paramref name="wait"/> is set, waits for the answer until <paramref name="decisionTimeout"/>.
    /// </summary>
    /// <returns>Text to print to the hook's stdout, or null when there is nothing to print.</returns>
    public static async Task<string?> ForwardAsync(
        string pipeName,
        string agent,
        JsonElement payload,
        bool wait,
        int clientPid,
        string? clientApp,
        DateTimeOffset now,
        TimeSpan connectTimeout,
        TimeSpan decisionTimeout,
        string? project = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var line = Frames.EncodeHook(id, agent, now.ToUnixTimeMilliseconds(), clientPid, wait, payload, clientApp, project);

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
                return null; // deadline passed: fall back to the agent's own prompt
            }
        }
        if (responseLine is null) return null;

        var (responseId, output) = Frames.DecodeResponse(responseLine);
        return responseId == id && !string.IsNullOrEmpty(output) ? output : null;
    }

    /// <summary>
    /// When no server instance exists, ConnectAsync(timeout) keeps retrying until the full timeout (~200 ms per hook
    /// while the app is closed). Listing the pipe namespace costs about 3 ms, so check that first.
    /// </summary>
    private static bool PipeExists(string pipeName) =>
        Directory.EnumerateFileSystemEntries(@"\\.\pipe\")
            .Any(entry => string.Equals(Path.GetFileName(entry), pipeName, StringComparison.OrdinalIgnoreCase));
}
