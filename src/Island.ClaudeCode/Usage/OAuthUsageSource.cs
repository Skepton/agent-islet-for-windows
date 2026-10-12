using System.Net;
using System.Net.Http.Headers;
using Island.Core;

namespace Island.ClaudeCode;

/// <summary>
/// Polls the (unofficial) endpoint Claude Code's /status uses. The token is read per call, sent only in the
/// Authorization header, and never logged: log lines carry status codes and exception type names only.
/// </summary>
public sealed class OAuthUsageSource : IUsageSource
{
    public static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");

    private readonly HttpClient _http;
    private readonly OAuthCredentialsReader _credentials;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;
    private readonly TimeSpan _timeout;

    public OAuthUsageSource(
        HttpClient http,
        OAuthCredentialsReader credentials,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null,
        TimeSpan? timeout = null)
    {
        _http = http;
        _credentials = credentials;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<UsageFetchResult> FetchAsync(CancellationToken ct)
    {
        var credentials = _credentials.Read();
        switch (credentials.Status)
        {
            case CredentialsStatus.Found:
                break;
            case CredentialsStatus.Expired:
                _log?.Invoke("usage poll skipped: access token expired (Claude Code refreshes it when used)");
                return new UsageFetchResult(UsageFetchStatus.TokenExpired);
            case CredentialsStatus.Unreadable:
                _log?.Invoke("usage poll skipped: credentials file unreadable");
                return new UsageFetchResult(UsageFetchStatus.TransientError);
            default:
                return new UsageFetchResult(UsageFetchStatus.Unavailable);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token!.Reveal());
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("claude-code-island/0.1");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);

        try
        {
            using var response = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);
            _log?.Invoke($"usage poll: HTTP {(int)response.StatusCode}");

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                {
                    var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                    var now = _clock();
                    var snapshot = UsageParser.ParseOAuthUsage(body, now);
                    return snapshot is null
                        ? new UsageFetchResult(UsageFetchStatus.SchemaChanged)
                        : new UsageFetchResult(UsageFetchStatus.Ok, snapshot);
                }
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    return new UsageFetchResult(UsageFetchStatus.Unauthorized);
                case HttpStatusCode.TooManyRequests:
                    return new UsageFetchResult(UsageFetchStatus.RateLimited, RetryAfter: ReadRetryAfter(response));
                default:
                    return new UsageFetchResult(UsageFetchStatus.TransientError);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke("usage poll: timed out");
            return new UsageFetchResult(UsageFetchStatus.TransientError);
        }
        catch (HttpRequestException ex)
        {
            _log?.Invoke($"usage poll failed ({ex.GetType().Name})");
            return new UsageFetchResult(UsageFetchStatus.TransientError);
        }
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - _clock();
        return null;
    }
}
