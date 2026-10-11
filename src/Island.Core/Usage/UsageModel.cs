namespace Island.Core;

public enum UsageWindowId { FiveHour, SevenDay }

/// <param name="Percent">0-100.</param>
public sealed record UsageWindow(UsageWindowId Id, double Percent, DateTimeOffset ResetsAt);

public sealed record UsageSnapshot(DateTimeOffset At, IReadOnlyList<UsageWindow> Windows)
{
    public UsageWindow? Get(UsageWindowId id) => Windows.FirstOrDefault(w => w.Id == id);
}

public enum UsageFetchStatus
{
    Ok,

    /// No usable credentials (not logged in with claude.ai, or an API-key user). The feature stays quietly off.
    Unavailable,

    /// The stored access token has expired. Claude Code refreshes it when it next runs; we never refresh it.
    TokenExpired,

    /// The server rejected the token (401/403).
    Unauthorized,

    RateLimited,

    /// Network error, timeout, 5xx, or an unreadable credentials file. Worth retrying later.
    TransientError,

    /// The response was not in a shape we understand (the endpoint is unofficial and may change).
    SchemaChanged,
}

public sealed record UsageFetchResult(UsageFetchStatus Status, UsageSnapshot? Snapshot = null, TimeSpan? RetryAfter = null);

public interface IUsageSource
{
    Task<UsageFetchResult> FetchAsync(CancellationToken ct);
}
