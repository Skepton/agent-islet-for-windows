using System.Text.Json;
using Island.Core;

namespace Island.ClaudeCode;

/// <summary>
/// The subset of a Claude Code hook's stdin JSON that the island uses. Parsed tolerantly:
/// unknown fields are ignored and wrong-typed optional fields are treated as absent.
/// </summary>
public sealed record HookPayload
{
    public string EventName { get; init; } = "";
    public string? SessionId { get; init; }
    public string? Cwd { get; init; }
    public string? PermissionMode { get; init; }
    public string? ToolName { get; init; }
    public string? ToolInputJson { get; init; }
    public IReadOnlyList<string> PermissionSuggestionsJson { get; init; } = Array.Empty<string>();
    public string? Message { get; init; }
    public string? NotificationType { get; init; }
    public string? LastAssistantMessage { get; init; }
    public bool StopHookActive { get; init; }
    public string? SessionTitle { get; init; }

    /// <summary>The user's prompt (UserPromptSubmit). Shown on cards, kept in memory only, never persisted.</summary>
    public string? Prompt { get; init; }

    public static HookPayload Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Hook payload is not a JSON object.");

        return new HookPayload
        {
            EventName = JsonUtil.Str(root, "hook_event_name") ?? "",
            SessionId = JsonUtil.Str(root, "session_id"),
            Cwd = JsonUtil.Str(root, "cwd"),
            PermissionMode = JsonUtil.Str(root, "permission_mode"),
            ToolName = JsonUtil.Str(root, "tool_name"),
            ToolInputJson = JsonUtil.Raw(root, "tool_input"),
            PermissionSuggestionsJson = JsonUtil.RawArray(root, "permission_suggestions"),
            Message = JsonUtil.Str(root, "message"),
            NotificationType = JsonUtil.Str(root, "notification_type"),
            LastAssistantMessage = JsonUtil.Str(root, "last_assistant_message"),
            StopHookActive = JsonUtil.Bool(root, "stop_hook_active"),
            SessionTitle = JsonUtil.Str(root, "session_title"),
            Prompt = JsonUtil.Str(root, "prompt"),
        };
    }
}
