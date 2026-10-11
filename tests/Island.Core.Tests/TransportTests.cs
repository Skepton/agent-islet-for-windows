using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Island.Core;

namespace Island.Core.Tests;

/// <summary>Hub and real named-pipe round trips, without any UI.</summary>
public class TransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static HookFrame FrameFor(string fixture, bool wait, string id) =>
        new(id, Now.ToUnixTimeMilliseconds(), 1, wait, File.ReadAllText(Payloads.Fixture("hooks", fixture)));

    private static string NewPipeName() => "island-test-" + Guid.NewGuid().ToString("N");

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Held_permission_returns_the_answer_given_by_the_ui()
    {
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new IslandHub(new SessionStore(), new PermissionBroker(), effects =>
        {
            foreach (var e in effects.OfType<PermissionShown>()) shown.TrySetResult(e.Permission.RequestId);
        });

        var pending = hub.HandleAsync(FrameFor("PermissionRequest.json", wait: true, id: "req-1"), CancellationToken.None);
        Assert.Equal("req-1", await shown.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        hub.Answer("req-1", PermissionDecision.Deny("nope"), PermissionOutcome.Answered);

        var result = await pending;
        Assert.NotNull(result);
        Assert.Contains("\"behavior\":\"deny\"", result);
    }

    [Fact]
    public async Task Answer_in_terminal_returns_no_decision()
    {
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new IslandHub(new SessionStore(), new PermissionBroker(), effects =>
        {
            foreach (var e in effects.OfType<PermissionShown>()) shown.TrySetResult(e.Permission.RequestId);
        });

        var pending = hub.HandleAsync(FrameFor("PermissionRequest.json", wait: true, id: "req-t"), CancellationToken.None);
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hub.Answer("req-t", null, PermissionOutcome.DeferredToTerminal);

        Assert.Null(await pending);
    }

    [Fact]
    public async Task Cancelled_permission_is_abandoned_and_returns_no_decision()
    {
        var cleared = new ConcurrentBag<PermissionCleared>();
        var hub = new IslandHub(new SessionStore(), new PermissionBroker(), effects =>
        {
            foreach (var e in effects.OfType<PermissionCleared>()) cleared.Add(e);
        });
        using var cts = new CancellationTokenSource();

        var pending = hub.HandleAsync(FrameFor("PermissionRequest.json", wait: true, id: "req-2"), cts.Token);
        cts.Cancel();

        Assert.Null(await pending);
        Assert.Contains(cleared, c => c.Outcome == PermissionOutcome.Abandoned);
    }

    [Fact]
    public async Task Forwarder_gets_the_allow_decision_through_the_real_pipe()
    {
        var pipe = NewPipeName();
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new IslandHub(new SessionStore(), new PermissionBroker(), effects =>
        {
            foreach (var e in effects.OfType<PermissionShown>()) shown.TrySetResult(e.Permission.RequestId);
        });
        using var cts = new CancellationTokenSource();
        var serving = new IslandServer(pipe, (frame, ct) => hub.HandleAsync(frame, ct)).RunAsync(cts.Token);

        var payload = File.ReadAllText(Payloads.Fixture("hooks", "PermissionRequest.json"));
        var client = IslandClient.ForwardHookAsync(pipe, payload, 4321, Now, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20));

        hub.Answer(await shown.Task.WaitAsync(TimeSpan.FromSeconds(10)), PermissionDecision.Allow(), PermissionOutcome.Answered);

        var output = await client;
        Assert.NotNull(output);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("allow", doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString());

        cts.Cancel();
        await serving;
    }

    [Fact]
    public async Task Non_blocking_event_is_delivered_and_returns_immediately()
    {
        var pipe = NewPipeName();
        var store = new SessionStore();
        var hub = new IslandHub(store, new PermissionBroker(), _ => { });
        using var cts = new CancellationTokenSource();
        var serving = new IslandServer(pipe, (frame, ct) => hub.HandleAsync(frame, ct)).RunAsync(cts.Token);

        var payload = File.ReadAllText(Payloads.Fixture("hooks", "Stop.json"));
        var clock = Stopwatch.StartNew();
        var output = await IslandClient.ForwardHookAsync(pipe, payload, 0, Now, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));

        Assert.Null(output);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"forwarder took {clock.Elapsed.TotalMilliseconds} ms");
        await Eventually(() => store.Snapshot().Count == 1);

        cts.Cancel();
        await serving;
    }

    [Fact]
    public async Task Forwarder_disconnect_drops_the_card()
    {
        var pipe = NewPipeName();
        var cleared = new ConcurrentBag<PermissionCleared>();
        var store = new SessionStore();
        var hub = new IslandHub(store, new PermissionBroker(), effects =>
        {
            foreach (var e in effects.OfType<PermissionCleared>()) cleared.Add(e);
        });
        using var cts = new CancellationTokenSource();
        var serving = new IslandServer(pipe, (frame, ct) => hub.HandleAsync(frame, ct)).RunAsync(cts.Token);

        var payload = File.ReadAllText(Payloads.Fixture("hooks", "PermissionRequest.json"));
        // Deadline shorter than the test: the forwarder gives up, closing the pipe.
        var output = await IslandClient.ForwardHookAsync(pipe, payload, 0, Now, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(300));

        Assert.Null(output);
        await Eventually(() => cleared.Any(c => c.Outcome == PermissionOutcome.Abandoned));
        Assert.Null(store.Snapshot().Single().Pending);

        cts.Cancel();
        await serving;
    }

    [Fact]
    public async Task Missing_app_fails_fast_so_the_hook_can_stay_silent()
    {
        var payload = File.ReadAllText(Payloads.Fixture("hooks", "PermissionRequest.json"));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<Exception>(() => IslandClient.ForwardHookAsync(
            "island-test-missing-" + Guid.NewGuid().ToString("N"), payload, 0, Now,
            TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed.TotalMilliseconds} ms");
    }
}
