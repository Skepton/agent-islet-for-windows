using System.Diagnostics;
using System.Text.Json;
using Island.Core;

namespace Island.ClaudeCode.Tests;

/// <summary>The forwarder's Claude Code side, end to end through a real pipe and hub.</summary>
public class ClaudeCodeHookTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static string NewPipeName() => "island-test-" + Guid.NewGuid().ToString("N");

    private static Func<string, string?> Env(string? entrypoint = "claude-vscode", string? pid = "4321") =>
        name => name switch
        {
            "CLAUDE_CODE_ENTRYPOINT" => entrypoint,
            "CLAUDE_PID" => pid,
            _ => null,
        };

    [Fact]
    public async Task Permission_request_prints_the_decision_chosen_on_the_island()
    {
        var pipe = NewPipeName();
        var shown = new TaskCompletionSource<PermissionShown>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new IslandHub(new SessionStore(), new PermissionBroker(), new IAgentIntegration[] { new ClaudeCodeAgent() }, effects =>
        {
            foreach (var e in effects.OfType<PermissionShown>()) shown.TrySetResult(e);
        });
        HookFrame? received = null;
        using var cts = new CancellationTokenSource();
        var serving = new IslandServer(pipe, (frame, ct) => { received = frame; return hub.HandleAsync(frame, ct); }).RunAsync(cts.Token);

        var client = ClaudeCodeHook.ForwardAsync(
            pipe, Payloads.Text("PermissionRequest.json"), Now, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(20), Env());

        var card = await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        hub.Answer(card.Permission.RequestId, PermissionVerdict.Allow, PermissionOutcome.Answered);

        var output = await client;
        Assert.NotNull(output);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("allow", doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString());
        Assert.Equal("VS Code", card.ClientApp);
        Assert.Equal(ClaudeCodeAgent.AgentId, received!.Agent);
        Assert.Equal(4321, received.ClientPid);

        cts.Cancel();
        await serving;
    }

    [Fact]
    public async Task Non_permission_events_do_not_wait()
    {
        var pipe = NewPipeName();
        var store = new SessionStore();
        var hub = new IslandHub(store, new PermissionBroker(), new IAgentIntegration[] { new ClaudeCodeAgent() }, _ => { });
        using var cts = new CancellationTokenSource();
        var serving = new IslandServer(pipe, (frame, ct) => hub.HandleAsync(frame, ct)).RunAsync(cts.Token);

        var clock = Stopwatch.StartNew();
        var output = await ClaudeCodeHook.ForwardAsync(pipe, Payloads.Text("Stop.json"), Now, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), Env());

        Assert.Null(output);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"forwarder took {clock.Elapsed.TotalMilliseconds} ms");

        cts.Cancel();
        await serving;
    }

    [Fact]
    public async Task Input_that_is_not_a_hook_payload_is_not_forwarded()
    {
        // No pipe exists: forwarding would throw, so a null result proves nothing was sent.
        Assert.Null(await ClaudeCodeHook.ForwardAsync(NewPipeName(), """{"hello":1}""", Now, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), Env()));
    }

    [Fact]
    public async Task A_byte_order_mark_on_stdin_is_tolerated()
    {
        // Parsing succeeds and it gets as far as looking for the (missing) app; a JSON error would not be an IOException.
        await Assert.ThrowsAnyAsync<IOException>(() => ClaudeCodeHook.ForwardAsync(
            NewPipeName(), "﻿" + Payloads.Text("Stop.json"), Now, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), Env()));
    }
}
