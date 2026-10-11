namespace Island.Core;

public sealed record MilestoneOptions
{
    /// <summary>Every 5% by default (5, 10, ... 100). A jump over several still gives one event, for the highest reached.</summary>
    public IReadOnlyList<int> Thresholds { get; init; } = Enumerable.Range(1, 20).Select(step => step * 5).ToArray();
    public bool FiveHourEnabled { get; init; } = true;
    public bool SevenDayEnabled { get; init; } = true;
}

public sealed record MilestoneEvent(UsageWindowId Window, int Threshold, double Percent, DateTimeOffset ResetsAt);

/// <summary>What has already been announced for one usage window.</summary>
public sealed record WindowProgress(int NotifiedThreshold, double LastPercent, DateTimeOffset ResetsAt);

/// <summary>Persisted milestone progress, keyed by window and reset time (so a new window starts fresh).</summary>
public sealed class MilestoneState
{
    public Dictionary<string, WindowProgress> Windows { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Decides when a usage reading deserves a popup. Rules:
/// at most one event per window per reading, for the highest threshold newly reached (never a burst);
/// each threshold fires once per window; a new reset time is a new window; lower readings than already seen are
/// ignored (out-of-order polls); the first reading announces only the highest threshold already crossed.
/// </summary>
public sealed class MilestoneEngine
{
    private static readonly TimeSpan KeepAfterReset = TimeSpan.FromDays(1);

    private readonly object _gate = new();
    private readonly MilestoneOptions _options;
    private readonly int[] _thresholds;
    private readonly Dictionary<string, WindowProgress> _progress;

    public MilestoneEngine(MilestoneOptions? options = null, MilestoneState? state = null)
    {
        _options = options ?? new MilestoneOptions();
        _thresholds = _options.Thresholds.Where(t => t is > 0 and <= 100).Distinct().OrderBy(t => t).ToArray();
        _progress = state is null
            ? new Dictionary<string, WindowProgress>(StringComparer.Ordinal)
            : new Dictionary<string, WindowProgress>(state.Windows, StringComparer.Ordinal);
    }

    public IReadOnlyList<MilestoneEvent> Process(UsageSnapshot snapshot, DateTimeOffset now)
    {
        var events = new List<MilestoneEvent>();
        lock (_gate)
        {
            foreach (var window in snapshot.Windows)
            {
                if (!IsEnabled(window.Id)) continue;

                var key = KeyFor(window);
                _progress.TryGetValue(key, out var seen);
                if (seen is not null && window.Percent < seen.LastPercent) continue; // older reading arriving late

                var reached = _thresholds.LastOrDefault(t => window.Percent >= t); // 0 when below the first threshold
                var notified = seen?.NotifiedThreshold ?? 0;
                if (reached > notified)
                {
                    events.Add(new MilestoneEvent(window.Id, reached, window.Percent, window.ResetsAt));
                    notified = reached;
                }
                _progress[key] = new WindowProgress(notified, window.Percent, window.ResetsAt);
            }

            foreach (var stale in _progress.Where(p => p.Value.ResetsAt < now - KeepAfterReset).Select(p => p.Key).ToList())
                _progress.Remove(stale);
        }
        return events;
    }

    public MilestoneState ExportState()
    {
        var copy = new MilestoneState();
        lock (_gate)
        {
            foreach (var (key, progress) in _progress) copy.Windows[key] = progress;
        }
        return copy;
    }

    private bool IsEnabled(UsageWindowId id) => id switch
    {
        UsageWindowId.FiveHour => _options.FiveHourEnabled,
        UsageWindowId.SevenDay => _options.SevenDayEnabled,
        _ => false,
    };

    /// <summary>
    /// The endpoint reports reset times with fractional seconds (16:10:00.076) and the status line with whole
    /// epoch seconds. Rounding to the minute makes both sources agree on which window a reading belongs to.
    /// </summary>
    private static string KeyFor(UsageWindow window) =>
        $"{window.Id}|{(long)Math.Round(window.ResetsAt.ToUnixTimeMilliseconds() / 60000.0)}";
}
