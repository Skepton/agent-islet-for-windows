using System.Collections.Concurrent;
using Island.Core;

namespace Island.Core.Tests;

public class PollScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly UsagePollOptions Plain = new() { Jitter = 0 };

    [Fact]
    public void Active_use_polls_every_two_minutes()
    {
        var delay = PollSchedule.NextDelay(Plain, Now, Now.AddMinutes(-3), 0, null, 0);

        Assert.Equal(TimeSpan.FromMinutes(2), delay);
    }

    [Fact]
    public void Idle_polls_every_fifteen_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), PollSchedule.NextDelay(Plain, Now, Now.AddMinutes(-11), 0, null, 0));
        Assert.Equal(TimeSpan.FromMinutes(15), PollSchedule.NextDelay(Plain, Now, null, 0, null, 0));
    }

    [Theory]
    [InlineData(1, 2)]   // active base (2 min) still larger than the first backoff (1 min)
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(6, 30)]  // capped
    [InlineData(40, 30)] // no overflow
    public void Failures_back_off_exponentially_up_to_the_cap(int failures, int expectedMinutes)
    {
        var delay = PollSchedule.NextDelay(Plain, Now, Now, failures, null, 0);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), delay);
    }

    [Fact]
    public void Retry_after_is_honoured_but_capped_at_an_hour()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), PollSchedule.NextDelay(Plain, Now, Now, 1, TimeSpan.FromMinutes(5), 0));
        Assert.Equal(TimeSpan.FromHours(1), PollSchedule.NextDelay(Plain, Now, Now, 1, TimeSpan.FromHours(9), 0));
    }

    [Fact]
    public void Jitter_stays_within_ten_percent()
    {
        var options = new UsagePollOptions();

        var low = PollSchedule.NextDelay(options, Now, Now, 0, null, -1);
        var high = PollSchedule.NextDelay(options, Now, Now, 0, null, 1);
        var clamped = PollSchedule.NextDelay(options, Now, Now, 0, null, 50);

        Assert.Equal(TimeSpan.FromSeconds(108), low);
        Assert.Equal(TimeSpan.FromSeconds(132), high);
        Assert.Equal(high, clamped);
    }
}

public sealed class UsageMonitorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FiveReset = new(2026, 10, 11, 16, 10, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "island-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeSource : IUsageSource
    {
        private readonly UsageFetchResult[] _results;
        private int _calls;

        public FakeSource(params UsageFetchResult[] results) => _results = results;

        public int Calls => Volatile.Read(ref _calls);

        public Task<UsageFetchResult> FetchAsync(CancellationToken ct)
        {
            var index = Math.Min(Interlocked.Increment(ref _calls) - 1, _results.Length - 1);
            return Task.FromResult(_results[index]);
        }
    }

    private sealed class ThrowingSource : IUsageSource
    {
        public Task<UsageFetchResult> FetchAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    private static UsageFetchResult Ok(double five) =>
        new(UsageFetchStatus.Ok, new UsageSnapshot(Now, new[] { new UsageWindow(UsageWindowId.FiveHour, five, FiveReset) }));

    private static UsageFetchResult Failed(UsageFetchStatus status = UsageFetchStatus.TransientError) => new(status);

    private static UsageMonitor Monitor(IUsageSource source, List<UsageUpdate> updates, MilestoneStateStore? store = null, UsagePollOptions? options = null) =>
        new(source, new MilestoneEngine(), updates.Add, store, options, () => Now, () => 0);

    [Fact]
    public async Task A_successful_poll_reports_milestones_and_the_latest_snapshot()
    {
        var updates = new List<UsageUpdate>();
        var monitor = Monitor(new FakeSource(Ok(28)), updates);

        var update = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(25, Assert.Single(update.Milestones).Threshold);
        Assert.Equal(28, update.Latest!.Get(UsageWindowId.FiveHour)!.Percent);
        Assert.Equal(0, update.ConsecutiveFailures);
        Assert.Same(update, Assert.Single(updates));
    }

    [Fact]
    public async Task A_failed_poll_keeps_the_last_good_snapshot_and_counts_the_failure()
    {
        var updates = new List<UsageUpdate>();
        var monitor = Monitor(new FakeSource(Ok(28), Failed(), Failed(UsageFetchStatus.RateLimited), Ok(29)), updates);

        await monitor.PollOnceAsync(CancellationToken.None);
        var first = await monitor.PollOnceAsync(CancellationToken.None);
        var second = await monitor.PollOnceAsync(CancellationToken.None);
        var recovered = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, first.ConsecutiveFailures);
        Assert.Equal(28, first.Latest!.Get(UsageWindowId.FiveHour)!.Percent);
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Empty(recovered.Milestones);
    }

    [Fact]
    public async Task Not_being_logged_in_is_not_counted_as_a_failure()
    {
        var updates = new List<UsageUpdate>();
        var monitor = Monitor(new FakeSource(Failed(UsageFetchStatus.Unavailable)), updates);

        var update = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(0, update.ConsecutiveFailures);
        Assert.Null(update.Latest);
    }

    [Fact]
    public async Task A_source_that_throws_is_treated_as_a_transient_failure()
    {
        var updates = new List<UsageUpdate>();
        var monitor = Monitor(new ThrowingSource(), updates);

        var update = await monitor.PollOnceAsync(CancellationToken.None);

        Assert.Equal(UsageFetchStatus.TransientError, update.Result.Status);
        Assert.Equal(1, update.ConsecutiveFailures);
    }

    [Fact]
    public async Task Progress_is_saved_after_a_successful_poll_so_a_restart_does_not_repeat_milestones()
    {
        var path = Path.Combine(_dir, "state.json");
        var store = new MilestoneStateStore(path);

        await Monitor(new FakeSource(Ok(55)), new List<UsageUpdate>(), store).PollOnceAsync(CancellationToken.None);
        Assert.True(File.Exists(path));

        var restartedUpdates = new List<UsageUpdate>();
        var restarted = new UsageMonitor(new FakeSource(Ok(56)), new MilestoneEngine(state: store.Load()), restartedUpdates.Add, store, clock: () => Now, jitter: () => 0);
        var update = await restarted.PollOnceAsync(CancellationToken.None);

        Assert.Empty(update.Milestones);
    }

    [Fact]
    public async Task Run_polls_repeatedly_and_stops_when_cancelled()
    {
        var source = new FakeSource(Ok(5));
        var options = new UsagePollOptions { ActiveInterval = TimeSpan.FromMilliseconds(30), IdleInterval = TimeSpan.FromMilliseconds(30), Jitter = 0 };
        var monitor = new UsageMonitor(source, new MilestoneEngine(), _ => { }, options: options);
        using var cts = new CancellationTokenSource();

        var running = monitor.RunAsync(cts.Token);
        await Task.Delay(400);
        cts.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(source.Calls >= 3, $"only {source.Calls} polls");
    }

    [Fact]
    public async Task Activity_ends_an_idle_wait_early()
    {
        var source = new FakeSource(Ok(5));
        var options = new UsagePollOptions
        {
            ActiveInterval = TimeSpan.FromMilliseconds(40),
            IdleInterval = TimeSpan.FromSeconds(60),
            ActivityWindow = TimeSpan.FromSeconds(30),
            Jitter = 0,
        };
        var monitor = new UsageMonitor(source, new MilestoneEngine(), _ => { }, options: options);
        using var cts = new CancellationTokenSource();

        var running = monitor.RunAsync(cts.Token);
        await Task.Delay(250);
        Assert.Equal(1, source.Calls); // idle: only the first poll so far

        monitor.NoteActivity();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (source.Calls < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
        cts.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(source.Calls >= 2, "activity did not trigger an earlier poll");
    }

    [Fact]
    public async Task Concurrent_updates_all_reach_the_callback()
    {
        var seen = new ConcurrentBag<UsageUpdate>();
        var monitor = new UsageMonitor(new FakeSource(Ok(5)), new MilestoneEngine(), seen.Add, clock: () => Now, jitter: () => 0);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => monitor.PollOnceAsync(CancellationToken.None)));

        Assert.Equal(20, seen.Count);
    }
}
