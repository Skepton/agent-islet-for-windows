using System.Text.Json;
using Island.Core;

namespace Island.Core.Tests;

internal static class Payloads
{
    /// <summary>Path to a file or folder under tests/fixtures (copied next to the test binaries).</summary>
    public static string Fixture(params string[] parts) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", Path.Combine(parts));
}

/// <summary>Neutral events for driving the store directly, with the defaults most tests want.</summary>
internal static class Events
{
    public const string Cwd = "C:\\work\\my-app";

    public static PendingPermission Pending(string requestId, string tool = "Bash", string summary = "npm test", string? description = null) =>
        new(requestId, tool, summary, "default", Array.Empty<RememberOption>(), description, $"Agent wants to run {tool}.", "$");

    public static PermissionRequested Permission(string requestId, string summary = "npm test", string session = "s1") =>
        new() { SessionId = session, Cwd = Cwd, Permission = Pending(requestId, summary: summary) };

    public static ToolProceeded Proceeded(string session = "s1") => new() { SessionId = session, Cwd = Cwd };

    public static InputRequested Input(string text, bool echoesPermission = false, string session = "s1") =>
        new() { SessionId = session, Cwd = Cwd, Text = text, EchoesPermission = echoesPermission };

    public static TurnFinished Finished(string? message, bool continuing = false, string session = "s1") =>
        new() { SessionId = session, Cwd = Cwd, Message = message, Continuing = continuing };

    public static PromptSubmitted Prompt(string prompt, string session = "s1") =>
        new() { SessionId = session, Cwd = Cwd, Prompt = prompt };

    public static SessionStarted Started(string session = "s1") => new() { SessionId = session, Cwd = Cwd };

    public static SessionEnded Ended(string session = "s1") => new() { SessionId = session, Cwd = Cwd };
}

/// <summary>
/// A made-up second agent, so Core is tested without Claude Code. Payload: {"event":"…","session":"…","tool":"…","text":"…"}.
/// Verdicts encode as plain text: "allow", "deny:&lt;message&gt;", "remember:&lt;payload&gt;".
/// </summary>
internal sealed class TestAgent : IAgentIntegration
{
    public const string AgentId = "test-agent";

    public string Id => AgentId;

    public string DisplayName => "Test Agent";

    public static string Payload(string evt, string session = "s1", string tool = "Shell", string text = "") =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["event"] = evt, ["session"] = session, ["tool"] = tool, ["text"] = text });

    public AgentEvent? Translate(string payloadJson, string requestId)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        if (JsonUtil.Str(root, "session") is not { Length: > 0 } sid) return null;
        var tool = JsonUtil.Str(root, "tool") ?? "tool";
        var text = JsonUtil.Str(root, "text") ?? "";

        return JsonUtil.Str(root, "event") switch
        {
            "permission" => new PermissionRequested
            {
                SessionId = sid,
                Permission = new PendingPermission(requestId, tool, text, "", new[] { new RememberOption("Always", "Always allow " + tool, "rule:" + tool) }),
            },
            "proceeded" => new ToolProceeded { SessionId = sid },
            "finished" => new TurnFinished { SessionId = sid, Message = text },
            _ => new SessionActivity { SessionId = sid },
        };
    }

    /// <summary>A "permission" event waits; headers X-App and X-Project give the client app and project.</summary>
    public HookFrame? FrameFromHttp(string requestId, HookHttpRequest request, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(request.Body);
        var evt = JsonUtil.Str(doc.RootElement, "event");
        if (evt is null) return null;
        request.Headers.TryGetValue("X-App", out var app);
        request.Headers.TryGetValue("X-Project", out var project);
        return new HookFrame(requestId, AgentId, now.ToUnixTimeMilliseconds(), 0, evt == "permission", request.Body, app, project);
    }

    public string? EncodeVerdict(PermissionVerdict verdict) => verdict switch
    {
        AllowVerdict => "allow",
        DenyVerdict deny => "deny:" + deny.Message,
        RememberVerdict remember => "remember:" + remember.Option.Payload,
        _ => null,
    };
}
