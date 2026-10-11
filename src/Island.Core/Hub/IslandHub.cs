namespace Island.Core;

/// <summary>
/// Glue between incoming hook frames, the session store and the presentation layer.
/// Holds PermissionRequests until the UI answers, the forwarder disconnects, or the request is superseded.
/// </summary>
public sealed class IslandHub
{
    private readonly SessionStore _store;
    private readonly PermissionBroker _broker;
    private readonly Action<IReadOnlyList<StoreEffect>> _present;
    private readonly Func<DateTimeOffset> _clock;

    public IslandHub(
        SessionStore store,
        PermissionBroker broker,
        Action<IReadOnlyList<StoreEffect>> present,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _broker = broker;
        _present = present;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<SessionInfo> Sessions => _store.Snapshot();

    /// <returns>The decision JSON for a held PermissionRequest; null for everything else.</returns>
    public async Task<string?> HandleAsync(HookFrame frame, CancellationToken ct)
    {
        var hook = HookPayload.Parse(frame.PayloadJson) with { ClientApp = frame.ClientApp };
        var holdForDecision = frame.Wait && hook.EventName == "PermissionRequest";

        // Register the waiter before anything is presented, so an answer given instantly cannot be lost.
        var decision = holdForDecision ? _broker.HoldAsync(frame.Id, ct) : null;
        Dispatch(_store.Apply(hook, frame.Id, _clock()));
        if (decision is null) return null;

        var answer = await decision.ConfigureAwait(false);
        if (!ct.IsCancellationRequested) return answer;

        // The forwarder went away (terminal answered, or its deadline passed): drop the card.
        Dispatch(_store.ResolvePermission(frame.Id, _clock(), PermissionOutcome.Abandoned));
        return null;
    }

    /// <summary>Called by the UI when the user acts on a permission card.</summary>
    /// <param name="decisionJson">Decision object (see <see cref="PermissionDecision"/>), or null for "answer in terminal".</param>
    public void Answer(string requestId, string? decisionJson, PermissionOutcome outcome)
    {
        _broker.Resolve(requestId, decisionJson);
        Dispatch(_store.ResolvePermission(requestId, _clock(), outcome));
    }

    /// <summary>Drops sessions that have been silent for hours. Call occasionally.</summary>
    public void Prune() => Dispatch(_store.Prune(_clock()));

    private void Dispatch(IReadOnlyList<StoreEffect> effects)
    {
        if (effects.Count == 0) return;

        foreach (var effect in effects)
        {
            // Any card that closes without an answer releases its held hook with "no decision".
            if (effect is PermissionCleared { Outcome: not PermissionOutcome.Answered } cleared)
                _broker.Resolve(cleared.RequestId, null);
        }
        _present(effects);
    }
}
