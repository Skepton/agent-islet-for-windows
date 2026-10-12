using System.Text;
using Island.ClaudeCode;
using Island.Core;
using Island.Hook;

// island-hook hook                      Claude Code hook: reads the event JSON on stdin.
// island-hook simulate <file> [secs]    Developer aid: replays a saved Claude Code payload through the pipe.
// island-hook install|uninstall [--settings <path>] [--dry-run]
//                                       Adds/removes the hooks in Claude Code's settings (see SetupCommand).
//
// Fail-open contract for hook/simulate: always exits 0. Any error (app not running, bad input, deadline)
// means silence on stdout, which Claude Code treats as "no decision".
// Another agent would get its own subcommands here, backed by its own integration library.

if (args is ["install", ..] or ["uninstall", ..]) return SetupCommand.Run(args);

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

        var output = await ClaudeCodeHook.ForwardAsync(
            IslandPipe.ResolveName(),
            input,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(200),
            decisionTimeout);

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
