using System.Collections.Concurrent;

namespace Island.Core;

/// <summary>
/// Holds a permission request's connection open until a verdict arrives, the request is abandoned,
/// or it is cancelled. A null result always means "no decision": the agent shows its own prompt.
/// </summary>
public sealed class PermissionBroker
{
    private readonly ConcurrentDictionary<string, Waiter> _waiting = new(StringComparer.Ordinal);

    /// <param name="agent">Turns the verdict into the hook output the forwarder prints.</param>
    /// <returns>The text to print, or null for nothing.</returns>
    public async Task<string?> HoldAsync(string requestId, IAgentIntegration agent, CancellationToken ct)
    {
        // Registration happens synchronously, before the caller shows anything, so an immediate answer is never lost.
        var waiter = new Waiter(new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously), agent);
        _waiting[requestId] = waiter;
        using var registration = ct.Register(static state => ((TaskCompletionSource<string?>)state!).TrySetResult(null), waiter.Done);
        try
        {
            return await waiter.Done.Task.ConfigureAwait(false);
        }
        finally
        {
            _waiting.TryRemove(new KeyValuePair<string, Waiter>(requestId, waiter));
        }
    }

    /// <param name="verdict">Null means "no decision".</param>
    /// <returns>True if a waiter existed and was completed by this call.</returns>
    public bool Resolve(string requestId, PermissionVerdict? verdict) =>
        _waiting.TryGetValue(requestId, out var waiter) && waiter.Done.TrySetResult(Encode(waiter.Agent, verdict));

    private static string? Encode(IAgentIntegration agent, PermissionVerdict? verdict)
    {
        if (verdict is null) return null;
        try
        {
            return agent.EncodeVerdict(verdict);
        }
        catch (Exception)
        {
            return null; // fail open: the agent asks the user itself
        }
    }

    private sealed record Waiter(TaskCompletionSource<string?> Done, IAgentIntegration Agent);
}
