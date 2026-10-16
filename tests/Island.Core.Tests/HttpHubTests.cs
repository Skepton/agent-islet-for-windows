using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using Island.Core;

namespace Island.Core.Tests;

/// <summary>An agent hook arriving over HTTP goes through the hub exactly like one from the pipe.</summary>
public sealed class HttpHubTests : IAsyncDisposable
{
    private const string Token = "tok";

    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentQueue<StoreEffect> _effects = new();
    private readonly IslandHub _hub;
    private readonly string _baseUrl;

    public HttpHubTests()
    {
        _hub = new IslandHub(new SessionStore(), new PermissionBroker(), new IAgentIntegration[] { new TestAgent() },
            effects => { foreach (var e in effects) _effects.Enqueue(e); });
        var server = new HookHttpServer(0, Token, (agent, request, ct) => _hub.HandleHttpAsync(agent, request, ct));
        _baseUrl = $"http://127.0.0.1:{server.Start()}";
        _ = server.RunAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _http.Dispose();
        _stop.Dispose();
    }

    private Task<HttpResponseMessage> Post(string agent, string payload, CancellationToken ct = default) =>
        _http.PostAsync($"{_baseUrl}/hook/{agent}/{Token}", new StringContent(payload, Encoding.UTF8, "application/json"), ct);

    private async Task<T> Eventually<T>(Func<T?> find) where T : class
    {
        for (var i = 0; i < 100; i++)
        {
            if (find() is { } found) return found;
            await Task.Delay(50);
        }
        throw new TimeoutException("the expected effect never appeared");
    }

    [Fact]
    public async Task A_finished_turn_reaches_the_store_and_the_answer_is_empty()
    {
        var response = await Post(TestAgent.AgentId, TestAgent.Payload("finished", text: "all done"));

        Assert.Equal("", await response.Content.ReadAsStringAsync());
        var notice = await Eventually(() => _effects.OfType<NoticeShown>().FirstOrDefault());
        Assert.Equal("all done", notice.Text);
    }

    [Fact]
    public async Task A_permission_request_waits_and_the_answer_given_on_the_island_is_the_response()
    {
        var pending = Post(TestAgent.AgentId, TestAgent.Payload("permission", tool: "Shell", text: "npm test"));
        var shown = await Eventually(() => _effects.OfType<PermissionShown>().FirstOrDefault());
        Assert.False(pending.IsCompleted); // still held: nobody has answered

        _hub.Answer(shown.Permission.RequestId, PermissionVerdict.Allow, PermissionOutcome.Answered);

        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("allow", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_waiting_request_is_dropped_when_the_agent_hangs_up()
    {
        using var hangUp = new CancellationTokenSource();
        var pending = Post(TestAgent.AgentId, TestAgent.Payload("permission"), hangUp.Token);
        var shown = await Eventually(() => _effects.OfType<PermissionShown>().FirstOrDefault());

        await hangUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var cleared = await Eventually(() => _effects.OfType<PermissionCleared>().FirstOrDefault(c => c.RequestId == shown.Permission.RequestId));
        Assert.Equal(PermissionOutcome.Abandoned, cleared.Outcome);
    }

    [Fact]
    public async Task An_agent_the_app_does_not_know_gets_an_empty_answer_and_changes_nothing()
    {
        var response = await Post("some-other-agent", TestAgent.Payload("finished"));

        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Empty(_effects);
    }

    [Fact]
    public async Task A_payload_the_agent_cannot_use_changes_nothing()
    {
        var response = await Post(TestAgent.AgentId, """{"unrelated":true}""");

        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Empty(_effects);
    }
}
