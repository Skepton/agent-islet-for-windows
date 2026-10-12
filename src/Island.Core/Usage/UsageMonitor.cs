namespace Island.Core;

/// <param name="Latest">The last successful snapshot, kept across failed polls.</param>
public sealed record UsageUpdate(
    UsageFetchResult Result,
    IReadOnlyList<MilestoneEvent> Milestones,
    UsageSnapshot? Latest,
    int ConsecutiveFailures);

/// <summary>
/// Polls a usage source on the <see cref="PollSchedule"/>, runs readings through the milestone engine, persists
/// progress, and reports every poll to a callback. Call <see cref="NoteActivity"/> on each hook event so it polls
/// more often while an agent is in use. The caller decides whether to start it (consent lives in the UI).
/// </summary>
public sealed class UsageMonitor
{
    private readonly IUsageSource _source;
    private readonly MilestoneEngine _engine;
    private readonly MilestoneStateStore? _store;
    private readonly UsagePollOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<double> _jitter;
    private readonly Action<UsageUpdate> _onUpdate;
    private readonly object _gate = new();

    private DateTimeOffset? _lastActivity;
    private DateTimeOffset? _lastPollAt;
    private int _failures;
    private TimeSpan? _retryAfter;
    private UsageSnapshot? _latest;
    private TaskCompletionSource _wake = NewWake();

    public UsageMonitor(
        IUsageSource source,
        MilestoneEngine engine,
        Action<UsageUpdate> onUpdate,
        MilestoneStateStore? store = null,
        UsagePollOptions? options = null,
        Func<DateTimeOffset>? clock = null,
        Func<double>? jitter = null)
    {
        _source = source;
        _engine = engine;
        _onUpdate = onUpdate;
        _store = store;
        _options = options ?? new UsagePollOptions();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _jitter = jitter ?? (() => Random.Shared.NextDouble() * 2 - 1);
    }

    /// <summary>Tell the monitor an agent did something. Ends an idle wait early, but never polls more often than the active interval.</summary>
    public void NoteActivity()
    {
        TaskCompletionSource? toWake = null;
        lock (_gate)
        {
            var now = _clock();
            var wasActive = PollSchedule.IsActive(_options, now, _lastActivity);
            _lastActivity = now;
            if (!wasActive) toWake = _wake;
        }
        toWake?.TrySetResult();
    }

    public async Task<UsageUpdate> PollOnceAsync(CancellationToken ct)
    {
        UsageFetchResult result;
        try
        {
            result = await _source.FetchAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            result = new UsageFetchResult(UsageFetchStatus.TransientError);
        }

        IReadOnlyList<MilestoneEvent> milestones = Array.Empty<MilestoneEvent>();
        UsageUpdate update;
        lock (_gate)
        {
            _lastPollAt = _clock();
            if (result is { Status: UsageFetchStatus.Ok, Snapshot: { } snapshot })
            {
                _failures = 0;
                _retryAfter = null;
                _latest = snapshot;
                milestones = _engine.Process(snapshot, _lastPollAt.Value);
            }
            else
            {
                // "Not signed in with a subscription" is a steady state, not an error worth backing off for.
                if (result.Status != UsageFetchStatus.Unavailable) _failures++;
                _retryAfter = result.RetryAfter;
            }
            update = new UsageUpdate(result, milestones, _latest, _failures);
        }

        if (_store is not null && result.Status == UsageFetchStatus.Ok)
        {
            try
            {
                _store.Save(_engine.ExportState());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing progress only means a threshold could be announced twice after a restart.
            }
        }

        _onUpdate(update);
        return update;
    }

    /// <summary>Polls now, then keeps polling on schedule until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
                await WaitUntilDueAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped.
        }
    }

    private async Task WaitUntilDueAsync(CancellationToken ct)
    {
        var jitter = _jitter(); // one sample per cycle so repeated recomputation stays stable
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            Task wakeTask;
            lock (_gate)
            {
                var now = _clock();
                var delay = PollSchedule.NextDelay(_options, now, _lastActivity, _failures, _retryAfter, jitter);
                wait = (_lastPollAt ?? now) + delay - now;
                _wake = NewWake();
                wakeTask = _wake.Task;
            }
            if (wait <= TimeSpan.Zero) return;

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(Task.Delay(wait, delayCts.Token), wakeTask).ConfigureAwait(false);
            await delayCts.CancelAsync().ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
