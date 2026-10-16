using Island.Core;

namespace Island.ClaudeCode;

/// <summary>
/// Claude Code's side of the island, inside the app: hook payloads in, PermissionRequest hook output out.
/// Event mapping (evidence in docs/findings.md):
/// SessionStart → started; UserPromptSubmit → prompt; PermissionRequest → permission card;
/// PostToolUse / PermissionDenied → the tool went ahead or was refused elsewhere; Notification of a "needs you" type →
/// input requested (permission_prompt only echoes the request); Stop → turn finished; SessionEnd → ended.
/// </summary>
public sealed class ClaudeCodeAgent : IAgentIntegration
{
    public const string AgentId = "claude-code";

    /// <summary>What Claude is told when the user clicks No on the island.</summary>
    public const string DefaultDenyMessage = "Denied from Agent Islet";

    // Notification types that mean "Claude is waiting for a human".
    private static readonly string[] NeedsYouTypes =
    [
        "permission_prompt", "idle_prompt", "elicitation_dialog", "elicitation_url_dialog", "agent_needs_input",
    ];

    public string Id => AgentId;

    public string DisplayName => "Claude Code";

    public AgentEvent? Translate(string payloadJson, string requestId)
    {
        var hook = HookPayload.Parse(payloadJson);
        if (string.IsNullOrEmpty(hook.SessionId)) return null;
        var sid = hook.SessionId;

        AgentEvent evt = hook.EventName switch
        {
            "SessionStart" => new SessionStarted { SessionId = sid },
            "UserPromptSubmit" => new PromptSubmitted { SessionId = sid, Prompt = hook.Prompt },
            "PermissionRequest" => new PermissionRequested { SessionId = sid, Permission = Pending(hook, requestId) },
            // PostToolUse carries no id that links it to its PermissionRequest; the call key does (see ToolSummary.CallKey).
            "PostToolUse" or "PermissionDenied" => new ToolProceeded { SessionId = sid, CallKey = ToolSummary.CallKey(hook.ToolName, hook.ToolInputJson) },
            // AskUserQuestion fires neither PermissionRequest nor a "needs you" Notification (seen live); PreToolUse
            // is registered for that tool only. Its PostToolUse clears the card once the user has answered.
            "PreToolUse" when hook.ToolName == "AskUserQuestion" => new InputRequested
            {
                SessionId = sid,
                Text = QuestionText.From(hook.ToolInputJson) ?? "Claude has a question for you",
                CallKey = ToolSummary.CallKey(hook.ToolName, hook.ToolInputJson),
            },
            "Notification" when hook.NotificationType is { } type && NeedsYouTypes.Contains(type) => new InputRequested
            {
                SessionId = sid,
                Text = hook.Message ?? type,
                // Claude Code sends this ~6 s after the PermissionRequest; it duplicates the card.
                EchoesPermission = type == "permission_prompt",
            },
            "Stop" => new TurnFinished { SessionId = sid, Message = hook.LastAssistantMessage, Continuing = hook.StopHookActive },
            "SessionEnd" => new SessionEnded { SessionId = sid },
            _ => new SessionActivity { SessionId = sid },
        };
        return evt with { Cwd = hook.Cwd, SessionTitle = hook.SessionTitle, Source = SourceOf(hook) };
    }

    /// <summary>
    /// A hook delivered over HTTP. The forwarder used to read three things from the hook's environment; the hook's headers
    /// now carry two of them (<c>X-Entrypoint</c>, <c>X-Claude-Pid</c>). <c>CLAUDE_PROJECT_DIR</c> is not available that
    /// way, so the project folder comes from the payload's <c>cwd</c> (a subagent's worktree maps back to its project).
    /// </summary>
    public HookFrame? FrameFromHttp(string requestId, HookHttpRequest request, DateTimeOffset now)
    {
        var hook = HookPayload.Parse(request.Body);
        if (string.IsNullOrEmpty(hook.EventName)) return null;

        request.Headers.TryGetValue("X-Entrypoint", out var entrypoint);
        request.Headers.TryGetValue("X-Claude-Pid", out var pidText);
        return new HookFrame(
            requestId,
            AgentId,
            now.ToUnixTimeMilliseconds(),
            int.TryParse(pidText, out var pid) ? pid : 0,
            Wait: hook.EventName == "PermissionRequest",
            PayloadJson: request.Body,
            ClientApp: ClientAppNames.FromEntrypoint(entrypoint),
            Project: ProjectFolder.OfWorktree(hook.Cwd));
    }

    /// <summary>"PostToolUse(Bash)", "Notification(idle_prompt)": event and tool / notification type names only.</summary>
    private static string SourceOf(HookPayload hook)
    {
        var detail = hook.EventName == "Notification" ? hook.NotificationType : hook.ToolName;
        var name = string.IsNullOrEmpty(hook.EventName) ? "(no event name)" : hook.EventName;
        return string.IsNullOrEmpty(detail) ? name : $"{name}({detail})";
    }

    public string? EncodeVerdict(PermissionVerdict verdict) => verdict switch
    {
        AllowVerdict => PermissionDecision.HookOutput(PermissionDecision.Allow()),
        DenyVerdict deny => PermissionDecision.HookOutput(PermissionDecision.Deny(deny.Message ?? DefaultDenyMessage)),
        RememberVerdict remember => PermissionDecision.HookOutput(PermissionDecision.Allow(new[] { remember.Option.Payload })),
        _ => null,
    };

    private static PendingPermission Pending(HookPayload hook, string requestId)
    {
        var tool = hook.ToolName ?? "tool";
        return new PendingPermission(
            requestId,
            tool,
            ToolSummary.Describe(hook.ToolName, hook.ToolInputJson),
            hook.PermissionMode ?? "",
            SuggestionChoice.FromRaw(hook.PermissionSuggestionsJson).Select(s => s.ToOption(tool)).ToList(),
            ToolSummary.Description(hook.ToolName, hook.ToolInputJson),
            ToolSummary.Headline(tool),
            ToolSummary.CommandPrefix(tool),
            ToolSummary.CallKey(hook.ToolName, hook.ToolInputJson));
    }
}
