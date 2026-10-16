using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Island.Core;

namespace Island.Core.Tests;

public sealed class HookHttpServerTests : IAsyncDisposable
{
    private const string Token = "secret-token";

    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly List<(string Agent, HookHttpRequest Request)> _seen = new();
    private Func<string, HookHttpRequest, CancellationToken, Task<string?>> _handler = (_, _, _) => Task.FromResult<string?>(null);
    private HookHttpServer? _server;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _http.Dispose();
        _stop.Dispose();
    }

    private string Start()
    {
        _server = new HookHttpServer(0, Token, (agent, request, ct) =>
        {
            lock (_seen) _seen.Add((agent, request));
            return _handler(agent, request, ct);
        });
        var port = _server.Start();
        _ = _server.RunAsync(_stop.Token);
        return $"http://127.0.0.1:{port}";
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_hook_post_reaches_the_handler_with_its_agent_body_and_headers()
    {
        var baseUrl = Start();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/hook/test-agent/{Token}") { Content = Json("{\"hook_event_name\":\"Stop\"}") };
        request.Headers.Add("X-Entrypoint", "claude-vscode");

        var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (agent, seen) = Assert.Single(_seen);
        Assert.Equal("test-agent", agent);
        Assert.Equal("{\"hook_event_name\":\"Stop\"}", seen.Body);
        Assert.Equal("claude-vscode", seen.Headers["x-entrypoint"]); // names are case-insensitive
    }

    [Fact]
    public async Task What_the_handler_returns_is_the_response_body()
    {
        _handler = (_, _, _) => Task.FromResult<string?>("{\"answer\":1}");
        var baseUrl = Start();

        var response = await _http.PostAsync($"{baseUrl}/hook/test-agent/{Token}", Json("{}"));

        Assert.Equal("{\"answer\":1}", await response.Content.ReadAsStringAsync());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task No_answer_is_an_empty_200()
    {
        var baseUrl = Start();

        var response = await _http.PostAsync($"{baseUrl}/hook/test-agent/{Token}", Json("{}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/hook/test-agent/wrong-token")]
    [InlineData("/hook/test-agent/")]
    [InlineData("/hook/test-agent")]
    [InlineData("/hook//secret-token")]
    [InlineData("/other/test-agent/secret-token")]
    [InlineData("/")]
    public async Task A_wrong_token_or_path_is_refused_before_the_handler_is_called(string path)
    {
        var baseUrl = Start();

        var response = await _http.PostAsync(baseUrl + path, Json("{}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_seen);
    }

    [Fact]
    public async Task Only_post_is_accepted()
    {
        var baseUrl = Start();

        var response = await _http.GetAsync($"{baseUrl}/hook/test-agent/{Token}");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(_seen);
    }

    [Fact]
    public async Task A_host_header_that_is_not_loopback_is_refused_so_dns_rebinding_cannot_reach_it()
    {
        var baseUrl = Start();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/hook/test-agent/{Token}") { Content = Json("{}") };
        request.Headers.Host = "evil.example.com";

        var response = await _http.SendAsync(request);

        Assert.Equal((HttpStatusCode)421, response.StatusCode);
        Assert.Empty(_seen);
    }

    [Fact]
    public async Task Localhost_as_the_host_name_is_fine()
    {
        var baseUrl = Start();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/hook/test-agent/{Token}") { Content = Json("{}") };
        request.Headers.Host = "localhost:1234";

        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_body_over_the_size_limit_is_refused()
    {
        var baseUrl = Start();
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", new Uri(baseUrl).Port);
        var stream = client.GetStream();
        var head = $"POST /hook/test-agent/{Token} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: {IslandPipe.MaxLineBytes + 1}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));

        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer);

        Assert.StartsWith("HTTP/1.1 413", Encoding.ASCII.GetString(buffer, 0, read));
        Assert.Empty(_seen);
    }

    [Fact]
    public async Task A_request_without_a_length_is_refused()
    {
        var baseUrl = Start();
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", new Uri(baseUrl).Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /hook/test-agent/{Token} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));

        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer);

        Assert.StartsWith("HTTP/1.1 411", Encoding.ASCII.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task A_request_still_waiting_is_cancelled_when_the_agent_hangs_up()
    {
        var cancelled = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        _handler = async (_, _, ct) =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
            }
            return null;
        };
        var baseUrl = Start();

        using var hangUp = new CancellationTokenSource();
        var pending = _http.PostAsync($"{baseUrl}/hook/test-agent/{Token}", Json("{}"), hangUp.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await hangUp.CancelAsync(); // the client closes its connection
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_throwing_handler_ends_the_connection_without_taking_the_server_down()
    {
        _handler = (_, _, _) => throw new InvalidOperationException("boom");
        var baseUrl = Start();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.PostAsync($"{baseUrl}/hook/test-agent/{Token}", Json("{}")));

        _handler = (_, _, _) => Task.FromResult<string?>("ok");
        var again = await _http.PostAsync($"{baseUrl}/hook/test-agent/{Token}", Json("{}"));
        Assert.Equal("ok", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Starting_on_a_port_that_is_taken_throws_so_the_caller_can_say_so()
    {
        var first = new HookHttpServer(0, Token, (_, _, _) => Task.FromResult<string?>(null));
        var port = first.Start();
        try
        {
            var second = new HookHttpServer(port, Token, (_, _, _) => Task.FromResult<string?>(null));
            Assert.Throws<SocketException>(() => second.Start());
        }
        finally
        {
            _stop.Cancel();
        }
    }

    [Fact]
    public void Tokens_are_long_unique_and_url_safe()
    {
        var a = HookHttpServer.NewToken();
        var b = HookHttpServer.NewToken();

        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 22);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
        Assert.Equal($"http://127.0.0.1:47821/hook/claude-code/{a}", HookHttpServer.UrlFor(47821, "claude-code", a));
    }
}
