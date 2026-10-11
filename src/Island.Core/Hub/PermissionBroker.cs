using System.Collections.Concurrent;

namespace Island.Core;

/// <summary>
/// Holds a PermissionRequest's connection open until a decision arrives, the request is abandoned,
/// or it is cancelled. A null result always means "no decision": Claude Code shows its own prompt.
/// </summary>
public sealed class PermissionBroker
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _waiting = new(StringComparer.Ordinal);

    public async Task<string?> HoldAsync(string requestId, CancellationToken ct)
    {
        // Registration happens synchronously, before the caller shows anything, so an immediate answer is never lost.
        var waiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[requestId] = waiter;
        using var registration = ct.Register(static state => ((TaskCompletionSource<string?>)state!).TrySetResult(null), waiter);
        try
        {
            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            _waiting.TryRemove(new KeyValuePair<string, TaskCompletionSource<string?>>(requestId, waiter));
        }
    }

    /// <returns>True if a waiter existed and was completed by this call.</returns>
    public bool Resolve(string requestId, string? decisionJson) =>
        _waiting.TryGetValue(requestId, out var waiter) && waiter.TrySetResult(decisionJson);
}
