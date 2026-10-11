using System.Text;
using Island.Core;

// island-hook hook                      Claude Code hook: reads the event JSON on stdin.
// island-hook simulate <file> [secs]    Developer aid: replays a saved payload through the pipe.
//
// Fail-open contract: always exits 0. Any error (app not running, bad input, deadline)
// means silence on stdout, which Claude Code treats as "no decision".

try
{
    var input = args switch
    {
        ["hook"] => await ReadStdinAsync(),
        ["simulate", var file, ..] => await File.ReadAllTextAsync(file),
        _ => null,
    };

    if (input is not null)
    {
        var decisionTimeout = args is [_, _, var secs, ..] && int.TryParse(secs, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(540); // hook timeout is 600 s; leave margin so we answer before Claude Code kills us

        var output = await IslandClient.ForwardHookAsync(
            IslandPipe.ResolveName(),
            input,
            ClientPid(),
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(200),
            decisionTimeout,
            ClientAppNames.FromEntrypoint(Environment.GetEnvironmentVariable("CLAUDE_CODE_ENTRYPOINT")));

        if (output is not null) await WriteStdoutAsync(output);
    }
}
catch (Exception)
{
    // Fail open: stay silent.
}

return 0;

static async Task<string> ReadStdinAsync()
{
    using var stdin = Console.OpenStandardInput();
    using var buffer = new MemoryStream();
    await stdin.CopyToAsync(buffer);
    return Encoding.UTF8.GetString(buffer.ToArray());
}

static async Task WriteStdoutAsync(string text)
{
    using var stdout = Console.OpenStandardOutput();
    await stdout.WriteAsync(Encoding.UTF8.GetBytes(text));
    await stdout.FlushAsync();
}

static int ClientPid() =>
    int.TryParse(Environment.GetEnvironmentVariable("CLAUDE_PID"), out var pid) ? pid : 0;
