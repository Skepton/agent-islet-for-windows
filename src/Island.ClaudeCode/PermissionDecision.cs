using System.Text;
using System.Text.Json;

namespace Island.ClaudeCode;

/// <summary>
/// Builds the decision objects Claude Code's PermissionRequest hook understands.
/// <see cref="Allow"/>/<see cref="Deny"/> return the decision object; <see cref="HookOutput"/> wraps it
/// in the hookSpecificOutput envelope the forwarder prints to stdout.
/// </summary>
public static class PermissionDecision
{
    /// <param name="rememberSuggestionsJson">
    /// Raw entries from permission_suggestions, echoed back unchanged as updatedPermissions ("allow and remember").
    /// </param>
    public static string Allow(IReadOnlyList<string>? rememberSuggestionsJson = null) => Build(w =>
    {
        w.WriteString("behavior", "allow");
        if (rememberSuggestionsJson is { Count: > 0 })
        {
            w.WriteStartArray("updatedPermissions");
            foreach (var suggestion in rememberSuggestionsJson) w.WriteRawValue(suggestion);
            w.WriteEndArray();
        }
    });

    public static string Deny(string? message = null, bool interrupt = false) => Build(w =>
    {
        w.WriteString("behavior", "deny");
        if (!string.IsNullOrWhiteSpace(message)) w.WriteString("message", message);
        if (interrupt) w.WriteBoolean("interrupt", true);
    });

    public static string HookOutput(string decisionJson) => Build(w =>
    {
        w.WriteStartObject("hookSpecificOutput");
        w.WriteString("hookEventName", "PermissionRequest");
        w.WritePropertyName("decision");
        w.WriteRawValue(decisionJson);
        w.WriteEndObject();
    });

    private static string Build(Action<Utf8JsonWriter> writeMembers)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            writeMembers(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
