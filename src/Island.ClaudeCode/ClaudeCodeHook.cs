using System.Text.Json;
using Island.Core;

namespace Island.ClaudeCode;

/// <summary>
/// Claude Code's side of the island, inside the forwarder (<c>island-hook hook</c>): reads what the hook gets
/// on stdin and in its environment, and sends it to the app. Kept tiny because it runs on every hook call.
/// </summary>
public static class ClaudeCodeHook
{
    /// <summary>Forwards one hook invocation. Only PermissionRequest waits for an answer.</summary>
    /// <param name="environment">Reads the hook's environment variables; the process environment by default.</param>
    /// <returns>Text to print to stdout, or null when there is nothing to print.</returns>
    public static async Task<string?> ForwardAsync(
        string pipeName,
        string stdinJson,
        DateTimeOffset now,
        TimeSpan connectTimeout,
        TimeSpan decisionTimeout,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        using var doc = JsonDocument.Parse(stdinJson.TrimStart('﻿'));
        var root = doc.RootElement;
        var eventName = JsonUtil.Str(root, "hook_event_name");
        if (eventName is null) return null; // not a hook payload: nothing to forward

        return await IslandClient.ForwardAsync(
            pipeName,
            ClaudeCodeAgent.AgentId,
            root,
            wait: eventName == "PermissionRequest",
            int.TryParse(environment("CLAUDE_PID"), out var pid) ? pid : 0,
            ClientAppNames.FromEntrypoint(environment("CLAUDE_CODE_ENTRYPOINT")),
            now,
            connectTimeout,
            decisionTimeout,
            environment("CLAUDE_PROJECT_DIR")).ConfigureAwait(false);
    }
}

/// <summary>Maps CLAUDE_CODE_ENTRYPOINT (set in the hook's environment) to a name for the card's chip.</summary>
public static class ClientAppNames
{
    public static string? FromEntrypoint(string? entrypoint) => entrypoint switch
    {
        "claude-vscode" => "VS Code",
        "cli" => "Terminal",
        "claude-desktop" => "Desktop",
        _ => null,
    };
}
