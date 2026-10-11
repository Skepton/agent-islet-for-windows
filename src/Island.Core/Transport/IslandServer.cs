using System.IO.Pipes;

namespace Island.Core;

/// <summary>
/// App-side named pipe server. One connection per hook invocation. Only the current user can connect.
/// A connection that closes while its request is held cancels that request, so the card is dropped
/// (e.g. the user answered in the terminal, or the forwarder hit its deadline).
/// </summary>
public sealed class IslandServer
{
    private static readonly TimeSpan FirstLineTimeout = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly Func<HookFrame, CancellationToken, Task<string?>> _onFrame;

    public IslandServer(string pipeName, Func<HookFrame, CancellationToken, Task<string?>> onFrame)
    {
        _pipeName = pipeName;
        _onFrame = onFrame;
    }

    /// <summary>Accepts connections until <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = CreateInstance();
                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    continue;
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                _ = ServeAsync(pipe, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown requested.
        }
    }

    private NamedPipeServerStream CreateInstance() => new(
        _pipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken shutdown)
    {
        await using (pipe)
        {
            try
            {
                string? line;
                using (var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown))
                {
                    readDeadline.CancelAfter(FirstLineTimeout);
                    line = await Frames.ReadLineAsync(pipe, readDeadline.Token).ConfigureAwait(false);
                }
                if (line is null) return;

                var frame = Frames.DecodeHook(line);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                if (frame.Wait) _ = WatchForDisconnectAsync(pipe, linked);

                var decision = await _onFrame(frame, linked.Token).ConfigureAwait(false);
                if (frame.Wait)
                {
                    await Frames.WriteLineAsync(pipe, Frames.EncodeResponse(frame.Id, decision), shutdown).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Fail open: a malformed or dropped connection just ends. The forwarder reads silence as "no decision".
            }
        }
    }

    /// <summary>The forwarder sends nothing after its frame, so the read only completes when it disconnects.</summary>
    private static async Task WatchForDisconnectAsync(Stream pipe, CancellationTokenSource linked)
    {
        var buffer = new byte[64];
        try
        {
            while (await pipe.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0)
            {
                // Ignore unexpected bytes; keep waiting for the disconnect.
            }
        }
        catch (Exception)
        {
            // Broken or closed pipe counts as a disconnect.
        }

        try
        {
            linked.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request had already completed and the connection was torn down.
        }
    }
}
