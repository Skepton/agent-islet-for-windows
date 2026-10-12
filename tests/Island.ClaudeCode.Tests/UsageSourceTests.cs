using System.Net;
using System.Text.Json.Nodes;
using Island.ClaudeCode;
using Island.Core;

namespace Island.ClaudeCode.Tests;

public sealed class UsageSourceTests : IDisposable
{
    private const string AccessToken = "SECRET-ACCESS-TOKEN-12345";
    private const string RefreshToken = "SECRET-REFRESH-TOKEN-98765";

    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "island-tests-" + Guid.NewGuid().ToString("N"));
    private string CredentialsPath => Path.Combine(_dir, ".credentials.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void WriteCredentials(string accessToken = AccessToken, long? expiresAtMs = null, bool includeOauth = true)
    {
        Directory.CreateDirectory(_dir);
        var root = new JsonObject();
        if (includeOauth)
        {
            root["claudeAiOauth"] = new JsonObject
            {
                ["accessToken"] = accessToken,
                ["refreshToken"] = RefreshToken,
                ["expiresAt"] = expiresAtMs ?? Now.AddHours(5).ToUnixTimeMilliseconds(),
                ["subscriptionType"] = "pro",
            };
        }
        File.WriteAllText(CredentialsPath, root.ToJsonString());
    }

    private OAuthCredentialsReader Reader() => new(CredentialsPath, () => Now);

    private sealed record SentRequest(HttpMethod Method, Uri? Uri, string? Authorization, string? Beta, string RawText);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _respond;

        public FakeHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public List<SentRequest> Sent { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add(new SentRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("anthropic-beta", out var beta) ? string.Join(",", beta) : null,
                request.ToString()));
            return await _respond(ct);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private (OAuthUsageSource Source, FakeHandler Handler, List<string> Log) Build(
        Func<CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null)
    {
        var handler = new FakeHandler(respond);
        var log = new List<string>();
        var source = new OAuthUsageSource(new HttpClient(handler), Reader(), () => Now, log.Add, timeout);
        return (source, handler, log);
    }

    private static string OkBody => File.ReadAllText(Payloads.Fixture("usage", "oauth-usage.response.json"));

    // --- credentials ---

    [Fact]
    public void Valid_credentials_yield_a_redacted_token()
    {
        WriteCredentials();

        var result = Reader().Read();

        Assert.Equal(CredentialsStatus.Found, result.Status);
        Assert.Equal(AccessToken, result.Token!.Reveal());
        Assert.DoesNotContain(AccessToken, result.Token.ToString());
        Assert.DoesNotContain(AccessToken, result.ToString());
    }

    [Fact]
    public void Missing_file_is_reported_as_missing()
    {
        Assert.Equal(CredentialsStatus.Missing, Reader().Read().Status);
    }

    [Fact]
    public void File_without_oauth_section_is_not_oauth()
    {
        WriteCredentials(includeOauth: false);

        Assert.Equal(CredentialsStatus.NotOAuth, Reader().Read().Status);
    }

    [Fact]
    public void Expired_token_is_reported_without_exposing_it()
    {
        WriteCredentials(expiresAtMs: Now.AddMinutes(-5).ToUnixTimeMilliseconds());

        var result = Reader().Read();

        Assert.Equal(CredentialsStatus.Expired, result.Status);
        Assert.Null(result.Token);
    }

    [Fact]
    public void Token_about_to_expire_counts_as_expired()
    {
        WriteCredentials(expiresAtMs: Now.AddSeconds(30).ToUnixTimeMilliseconds());

        Assert.Equal(CredentialsStatus.Expired, Reader().Read().Status);
    }

    [Fact]
    public void Garbage_file_is_unreadable_not_a_crash()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(CredentialsPath, "{ not json");

        Assert.Equal(CredentialsStatus.Unreadable, Reader().Read().Status);
    }

    [Fact]
    public void Reading_does_not_modify_the_credentials_file()
    {
        WriteCredentials();
        var before = File.ReadAllBytes(CredentialsPath);
        var stamp = File.GetLastWriteTimeUtc(CredentialsPath);

        Reader().Read();

        Assert.Equal(before, File.ReadAllBytes(CredentialsPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(CredentialsPath));
    }

    // --- the HTTP call ---

    [Fact]
    public async Task Successful_poll_sends_the_expected_request_and_parses_the_response()
    {
        WriteCredentials();
        var (source, handler, _) = Build(_ => Task.FromResult(Json(HttpStatusCode.OK, OkBody)));

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(UsageFetchStatus.Ok, result.Status);
        Assert.Equal(28.0, result.Snapshot!.Get(UsageWindowId.FiveHour)!.Percent);
        Assert.Equal(Now, result.Snapshot.At);

        var sent = Assert.Single(handler.Sent);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal(new Uri("https://api.anthropic.com/api/oauth/usage"), sent.Uri);
        Assert.Equal("Bearer " + AccessToken, sent.Authorization);
        Assert.Equal("oauth-2025-04-20", sent.Beta);
    }

    [Fact]
    public async Task The_refresh_token_is_never_sent_or_logged()
    {
        WriteCredentials();
        var (source, handler, log) = Build(_ => Task.FromResult(Json(HttpStatusCode.OK, OkBody)));

        await source.FetchAsync(CancellationToken.None);

        Assert.DoesNotContain(RefreshToken, handler.Sent.Single().RawText);
        Assert.DoesNotContain(log, line => line.Contains(RefreshToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"error\":\"x\"}", UsageFetchStatus.SchemaChanged)]
    [InlineData(HttpStatusCode.Unauthorized, "", UsageFetchStatus.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, "", UsageFetchStatus.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError, "", UsageFetchStatus.TransientError)]
    [InlineData(HttpStatusCode.BadGateway, "", UsageFetchStatus.TransientError)]
    [InlineData(HttpStatusCode.NotFound, "", UsageFetchStatus.TransientError)]
    public async Task Responses_map_to_statuses(HttpStatusCode code, string body, UsageFetchStatus expected)
    {
        WriteCredentials();
        var (source, _, _) = Build(_ => Task.FromResult(Json(code, body)));

        Assert.Equal(expected, (await source.FetchAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Rate_limit_response_carries_retry_after()
    {
        WriteCredentials();
        var (source, _, _) = Build(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        });

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(UsageFetchStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
    }

    [Fact]
    public async Task Network_failure_is_transient_and_the_log_has_no_message_text()
    {
        WriteCredentials();
        var (source, _, log) = Build(_ => throw new HttpRequestException("connection reset " + AccessToken));

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(UsageFetchStatus.TransientError, result.Status);
        Assert.DoesNotContain(log, line => line.Contains(AccessToken));
    }

    [Fact]
    public async Task A_hung_request_times_out_as_transient()
    {
        WriteCredentials();
        var (source, _, _) = Build(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json(HttpStatusCode.OK, OkBody);
        }, timeout: TimeSpan.FromMilliseconds(150));

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(UsageFetchStatus.TransientError, result.Status);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        WriteCredentials();
        using var cts = new CancellationTokenSource();
        var (source, _, _) = Build(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json(HttpStatusCode.OK, OkBody);
        });

        var pending = source.FetchAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Expired_token_makes_no_request()
    {
        WriteCredentials(expiresAtMs: Now.AddMinutes(-1).ToUnixTimeMilliseconds());
        var (source, handler, log) = Build(_ => Task.FromResult(Json(HttpStatusCode.OK, OkBody)));

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(UsageFetchStatus.TokenExpired, result.Status);
        Assert.Empty(handler.Sent);
        Assert.DoesNotContain(log, line => line.Contains(AccessToken));
    }

    [Fact]
    public async Task No_credentials_means_unavailable_and_no_request()
    {
        var (source, handler, _) = Build(_ => Task.FromResult(Json(HttpStatusCode.OK, OkBody)));

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(UsageFetchStatus.Unavailable, result.Status);
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task Nothing_the_source_returns_or_logs_contains_the_token()
    {
        WriteCredentials();
        var statuses = new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError };
        var log = new List<string>();
        var results = new List<UsageFetchResult>();

        foreach (var status in statuses)
        {
            var handler = new FakeHandler(_ => Task.FromResult(Json(status, status == HttpStatusCode.OK ? OkBody : "")));
            var source = new OAuthUsageSource(new HttpClient(handler), Reader(), () => Now, log.Add);
            results.Add(await source.FetchAsync(CancellationToken.None));
        }

        Assert.NotEmpty(log);
        Assert.DoesNotContain(log, line => line.Contains(AccessToken) || line.Contains(RefreshToken));
        Assert.DoesNotContain(results, r => r.ToString().Contains(AccessToken));
    }
}
