using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Island.Core;

namespace Island.Core.Tests;

/// <summary>Hub and real named-pipe round trips, without any UI, driven by <see cref="TestAgent"/>.</summary>
public class TransportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static HookFrame FrameFor(string payload, bool wait, string id, string agent = TestAgent.AgentId) =>
        new(id, agent, Now.ToUnixTimeMilliseconds(), 1, wait, payload);

    private static string NewPipeName() => "island-test-" + Guid.NewGuid().ToString("N");

    private static IslandHub Hub(Action<IReadOnlyList<StoreEffect>> present, SessionStore? store = null) =>
        new(store ?? new SessionStore(), new PermissionBroker(), new IAgentIntegration[] { new TestAgent() }, present);

    private static Task<string?> Forward(string pipe, string payload, bool wait, TimeSpan connect, TimeSpan decision)
    {
        using var doc = JsonDocument.Parse(payload);
        return IslandClient.ForwardAsync(pipe, TestAgent.AgentId, doc.RootElement.Clone(), wait, 0, null, Now, connect, decision);
    }

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
    public async Task Held_permission_returns_the_agents_encoding_of_the_answer()
    {
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = Hub(effects =>
        {
            foreach (var e in effects.OfType<PermissionShown>()) shown.TrySetResult(e.Permission.RequestId);
        });

        var pending = hub.HandleAsync(FrameFor(TestAgent.Payload("permission"), wait: true, id: "req-1"), CancellationToken.None);
        Assert.Equal("req-1", await shown.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        hub.Answer("req-1", PermissionVerdict.Deny("nope"), PermissionOutcome.Answered);

        Assert.Equal("deny:nope", await pending);
    }

    [Fact]
    public async Task Remember_hands_the_option_payload_back_to_the_agent()
    {
        PendingPermission? pendingPermission = null;
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = Hub(effects =>
        {
            foreach (var e in effects.OfType<PermissionShown>()) { pendingPermission = e.Permission; shown.TrySetResult(); }
        });

        var pending = hub.HandleAsync(FrameFor(TestAgent.Payload("permission", tool: "Shell"), wait: true, id: "req-r"), CancellationToken.None);
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hub.Answer("req-r", PermissionVerdict.Remember(pendingPermission!.Suggestions[0]), PermissionOutcome.Answered);

        Assert.Equal("remember:rule:Shell", await pending);
    }

    [Fact]
    public async Task Answer_in_terminal_returns_no_decision()
    {
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = Hub(effects =>
        {
            foreach (var e in effects.OfType<PermissionShown>()) shown.TrySetResult(e.Permission.RequestId);
        });

        var pending = hub.HandleAsync(FrameFor(TestAgent.Payload("permission"), wait: true, id: "req-t"), CancellationToken.None);
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hub.Answer("req-t", null, PermissionOutcome.DeferredToTerminal);

        Assert.Null(await pending);
    }

    [Fact]
    public async Task Cancelled_permission_is_abandoned_and_returns_no_decision()
    {
        var cleared = new ConcurrentBag<PermissionCleared>();
        var hub = Hub(effects =>
        {
            foreach (var e in effects.OfType<PermissionCleared>()) cleared.Add(e);
        });
        using var cts = new CancellationTokenSource();

        var pending = hub.HandleAsync(FrameFor(TestAgent.Payload("permission"), wait: true, id: "req-2"), cts.Token);
        cts.Cancel();

        Assert.Null(await pending);
        Assert.Contains(cleared, c => c.Outcome == PermissionOutcome.Abandoned);
    }

    [Fact]
    public async Task Frame_from_an_unknown_agent_is_ignored_even_when_it_waits()
    {
        var effects = new ConcurrentBag<StoreEffect>();
        var store = new SessionStore();
        var hub = Hub(e => { foreach (var x in e) effects.Add(x); }, store);

        var result = await hub.HandleAsync(FrameFor(TestAgent.Payload("permission"), wait: true, id: "r", agent: "someone-else"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(result);
        Assert.Empty(effects);
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public async Task Waiting_frame_that_is_not_a_permission_request_returns_at_once()
    {
        var hub = Hub(_ => { });

        var result = await hub.HandleAsync(FrameFor(TestAgent.Payload("finished"), wait: true, id: "f"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(result);
    }

    [Fact]
    public async Task Agent_name_and_client_app_reach_the_cards()
    {
        var shown = new TaskCompletionSource<PermissionShown>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = Hub(effects =>
        {
            foreach (var e in effects.OfType<PermissionShown>()) shown.TrySetResult(e);
        });

        _ = hub.HandleAsync(FrameFor(TestAgent.Payload("permission"), wait: true, id: "c") with { ClientApp = "Terminal" }, CancellationToken.None);
        var effect = await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Test Agent", effect.Agent);
        Assert.Equal("Terminal", effect.ClientApp);
        hub.Answer("c", null, PermissionOutcome.DeferredToTerminal);
    }

    [Fact]
    public async Task Forwarder_gets_the_output_through_the_real_pipe()
    {
        var pipe = NewPipeName();
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = Hub(effects =>
        {
            foreach (var e in effects.OfType<PermissionShown>()) shown.TrySetResult(e.Permission.RequestId);
        });
        using var cts = new CancellationTokenSource();
        var serving = new IslandServer(pipe, (frame, ct) => hub.HandleAsync(frame, ct)).RunAsync(cts.Token);

        var client = Forward(pipe, TestAgent.Payload("permission"), wait: true, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20));

        hub.Answer(await shown.Task.WaitAsync(TimeSpan.FromSeconds(10)), PermissionVerdict.Allow, PermissionOutcome.Answered);

        Assert.Equal("allow", await client);

        cts.Cancel();
        await serving;
    }

    [Fact]
    public async Task Non_blocking_event_is_delivered_and_returns_immediately()
    {
        var pipe = NewPipeName();
        var store = new SessionStore();
        var hub = Hub(_ => { }, store);
        using var cts = new CancellationTokenSource();
        var serving = new IslandServer(pipe, (frame, ct) => hub.HandleAsync(frame, ct)).RunAsync(cts.Token);

        var clock = Stopwatch.StartNew();
        var output = await Forward(pipe, TestAgent.Payload("finished", text: "done"), wait: false, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));

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
        var hub = Hub(effects =>
        {
            foreach (var e in effects.OfType<PermissionCleared>()) cleared.Add(e);
        }, store);
        using var cts = new CancellationTokenSource();
        var serving = new IslandServer(pipe, (frame, ct) => hub.HandleAsync(frame, ct)).RunAsync(cts.Token);

        // Deadline shorter than the test: the forwarder gives up, closing the pipe.
        var output = await Forward(pipe, TestAgent.Payload("permission"), wait: true, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(300));

        Assert.Null(output);
        await Eventually(() => cleared.Any(c => c.Outcome == PermissionOutcome.Abandoned));
        Assert.Null(store.Snapshot().Single().Pending);

        cts.Cancel();
        await serving;
    }

    [Fact]
    public async Task Missing_app_fails_fast_so_the_hook_can_stay_silent()
    {
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<Exception>(() => Forward(
            "island-test-missing-" + Guid.NewGuid().ToString("N"), TestAgent.Payload("permission"), wait: true,
            TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed.TotalMilliseconds} ms");
    }
}
