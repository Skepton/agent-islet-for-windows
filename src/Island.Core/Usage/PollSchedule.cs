namespace Island.Core;

public sealed record UsagePollOptions
{
    /// <summary>Poll this often while Claude Code has been active recently.</summary>
    public TimeSpan ActiveInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Poll this often when nothing has happened for a while.</summary>
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long after the last hook event we still count as "active".</summary>
    public TimeSpan ActivityWindow { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Fraction of the delay randomly added or removed, so installs do not poll in lockstep.</summary>
    public double Jitter { get; init; } = 0.10;
}

/// <summary>Pure scheduling rules for the usage poller.</summary>
public static class PollSchedule
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    public static bool IsActive(UsagePollOptions options, DateTimeOffset now, DateTimeOffset? lastActivity) =>
        lastActivity is { } last && now - last <= options.ActivityWindow;

    /// <param name="jitterSample">A number in [-1, 1]; scaled by <see cref="UsagePollOptions.Jitter"/>.</param>
    public static TimeSpan NextDelay(
        UsagePollOptions options,
        DateTimeOffset now,
        DateTimeOffset? lastActivity,
        int consecutiveFailures,
        TimeSpan? retryAfter,
        double jitterSample)
    {
        var delay = IsActive(options, now, lastActivity) ? options.ActiveInterval : options.IdleInterval;

        if (consecutiveFailures > 0)
        {
            var doubling = Math.Pow(2, Math.Min(consecutiveFailures - 1, 20));
            var backoff = TimeSpan.FromTicks((long)Math.Min(options.MaxBackoff.Ticks, options.MinBackoff.Ticks * doubling));
            if (backoff > delay) delay = backoff;
        }

        if (retryAfter is { } wait && wait > delay)
            delay = wait > MaxRetryAfter ? MaxRetryAfter : wait;

        var jitter = Math.Clamp(jitterSample, -1.0, 1.0) * options.Jitter;
        return TimeSpan.FromTicks((long)(delay.Ticks * (1 + jitter)));
    }
}
