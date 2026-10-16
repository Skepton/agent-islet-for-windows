using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Island.Core;

/// <summary>One hook call received over HTTP: the JSON body and the request headers (names are case-insensitive).</summary>
public sealed record HookHttpRequest(string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// The app's local web endpoint for agents whose hooks can call a URL (Claude Code's <c>http</c> hook type), so no helper
/// program has to be installed. It listens on the loopback address only and answers just
/// <c>POST /hook/&lt;agent&gt;/&lt;token&gt;</c>: the random token in the path keeps a web page, or another program, from
/// sending fake events or approvals, and a <c>Host</c> header that is not the loopback address is refused (DNS rebinding).
/// One request per connection (<c>Connection: close</c>). A request that is still waiting for the user is cancelled when
/// the agent closes its connection (the user answered in the terminal, or the agent's own timeout passed).
/// </summary>
public sealed class HookHttpServer
{
    public const string PathPrefix = "/hook/";

    private const int MaxHeaderBytes = 16 * 1024;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(5);

    private readonly string _token;
    private readonly Func<string, HookHttpRequest, CancellationToken, Task<string?>> _onRequest;
    private readonly TcpListener _listener;

    /// <param name="port">0 picks a free port (tests); read <see cref="Port"/> after <see cref="Start"/>.</param>
    /// <param name="onRequest">Called with the agent id from the path. Returns the response body (JSON the agent reads), or null for an empty 200.</param>
    public HookHttpServer(int port, string token, Func<string, HookHttpRequest, CancellationToken, Task<string?>> onRequest)
    {
        _token = token;
        _onRequest = onRequest;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port { get; private set; }

    /// <summary>A new random token: 128 bits, URL-safe.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The URL an agent's hook should call.</summary>
    public static string UrlFor(int port, string agentId, string token) => $"http://127.0.0.1:{port}{PathPrefix}{agentId}/{token}";

    /// <summary>Binds the port. Throws <see cref="SocketException"/> when it is taken.</summary>
    public int Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        return Port;
    }

    /// <summary>Accepts connections until <paramref name="ct"/> is cancelled. Call <see cref="Start"/> first.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var registration = ct.Register(() => _listener.Stop());
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (SocketException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException)
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    continue;
                }

                _ = ServeAsync(client, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown requested.
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken shutdown)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, shutdown).ConfigureAwait(false);
                if (request.Error is { } error)
                {
                    await RespondAsync(stream, error.Code, error.Reason, null).ConfigureAwait(false);
                    return;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                _ = WatchForDisconnectAsync(stream, linked);

                var output = await _onRequest(request.Agent!, request.Payload!, linked.Token).ConfigureAwait(false);
                if (linked.IsCancellationRequested) return; // the agent hung up: nobody is listening for an answer
                await RespondAsync(stream, 200, "OK", output).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Fail open: a malformed or dropped connection just ends. The agent treats that as "no decision".
            }
        }
    }

    private sealed record Parsed(string? Agent, HookHttpRequest? Payload, (int Code, string Reason)? Error)
    {
        public static Parsed Fail(int code, string reason) => new(null, null, (code, reason));
    }

    private async Task<Parsed> ReadRequestAsync(NetworkStream stream, CancellationToken shutdown)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(HeaderTimeout);

        var data = new List<byte>(2048);
        var chunk = new byte[4096];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, 0, chunk.Length, deadline.Token).ConfigureAwait(false);
            if (read == 0) return Parsed.Fail(400, "Bad Request");
            for (var i = 0; i < read; i++) data.Add(chunk[i]);
            if (data.Count > MaxHeaderBytes) return Parsed.Fail(431, "Request Header Fields Too Large");
            headerEnd = IndexOfHeaderEnd(data);
        }

        var head = Encoding.ASCII.GetString(data.GetRange(0, headerEnd).ToArray()).Split("\r\n");
        var requestLine = head[0].Split(' ');
        if (requestLine.Length < 2 || requestLine[0] != "POST") return Parsed.Fail(405, "Method Not Allowed");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < head.Length; i++)
        {
            var colon = head[i].IndexOf(':');
            if (colon > 0) headers[head[i][..colon].Trim()] = head[i][(colon + 1)..].Trim();
        }

        if (!IsLoopbackHost(headers.GetValueOrDefault("Host"))) return Parsed.Fail(421, "Misdirected Request");
        var agent = AgentFromPath(requestLine[1]);
        if (agent is null) return Parsed.Fail(404, "Not Found");

        if (headers.ContainsKey("Transfer-Encoding")) return Parsed.Fail(411, "Length Required");
        if (!headers.TryGetValue("Content-Length", out var lengthText) || !int.TryParse(lengthText, out var length) || length < 0)
            return Parsed.Fail(411, "Length Required");
        if (length > IslandPipe.MaxLineBytes) return Parsed.Fail(413, "Payload Too Large");

        var body = new byte[length];
        var have = Math.Min(length, data.Count - (headerEnd + 4));
        if (have > 0) data.CopyTo(headerEnd + 4, body, 0, have);
        while (have < length)
        {
            var read = await stream.ReadAsync(body, have, length - have, deadline.Token).ConfigureAwait(false);
            if (read == 0) return Parsed.Fail(400, "Bad Request");
            have += read;
        }

        return new Parsed(agent, new HookHttpRequest(Encoding.UTF8.GetString(body), headers), null);
    }

    /// <summary>The agent id when the path is <c>/hook/&lt;agent&gt;/&lt;our token&gt;</c>, else null.</summary>
    private string? AgentFromPath(string target)
    {
        var path = target.Split('?')[0];
        if (!path.StartsWith(PathPrefix, StringComparison.Ordinal)) return null;
        var parts = path[PathPrefix.Length..].Split('/');
        if (parts.Length != 2 || parts[0].Length == 0) return null;
        if (!TokenMatches(parts[1])) return null;
        return parts[0];
    }

    private bool TokenMatches(string candidate) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(_token));

    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var name = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        return name is "127.0.0.1" or "localhost" or "[::1]";
    }

    private static int IndexOfHeaderEnd(List<byte> data)
    {
        for (var i = 0; i + 3 < data.Count; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') return i;
        }
        return -1;
    }

    private static async Task RespondAsync(Stream stream, int code, string reason, string? body)
    {
        var payload = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {code} {reason}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
        var headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes).ConfigureAwait(false);
        if (payload.Length > 0) await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>The agent sends nothing after its request, so the read only completes when it disconnects.</summary>
    private static async Task WatchForDisconnectAsync(Stream stream, CancellationTokenSource linked)
    {
        var buffer = new byte[64];
        try
        {
            while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0)
            {
                // Ignore unexpected bytes; keep waiting for the disconnect.
            }
        }
        catch (Exception)
        {
            // A broken or closed connection counts as a disconnect.
        }

        try
        {
            linked.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The request had already completed and the connection was torn down.
        }
    }
}
