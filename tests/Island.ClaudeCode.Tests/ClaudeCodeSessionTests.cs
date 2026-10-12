using Island.Core;

namespace Island.ClaudeCode.Tests;

/// <summary>Claude Code payloads driven through the integration into the real session store.</summary>
public class ClaudeCodeSessionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private const string PromptPermissionMessage = "\"message\":\"Claude needs your permission to use Bash\"";

    private static IReadOnlyList<StoreEffect> Feed(SessionStore store, string payload, DateTimeOffset at, string requestId = "req") =>
        store.Apply(Payloads.Event(payload, requestId), at);

    [Fact]
    public void Permission_request_shows_a_card_with_its_summary_and_agent()
    {
        var store = new SessionStore();

        var effects = Feed(store, Payloads.Hook("PermissionRequest", Payloads.BashPermission("npm test")), T0, "req1");

        var shown = Assert.Single(effects.OfType<PermissionShown>());
        Assert.Equal("npm test", shown.Permission.Summary);
        Assert.Equal("Bash", shown.Permission.ToolName);
        Assert.Equal("my-app", shown.DisplayName);
        Assert.Equal("Claude Code", shown.Agent);
    }

    [Fact]
    public void Tool_running_after_a_permission_resolves_the_card_elsewhere()
    {
        var store = new SessionStore();
        Feed(store, Payloads.Hook("PermissionRequest", Payloads.BashPermission("x")), T0, "req1");

        var effects = Feed(store, Payloads.Hook("PostToolUse", ",\"tool_name\":\"Bash\""), T0.AddSeconds(3));

        Assert.Equal(PermissionOutcome.ResolvedElsewhere, Assert.Single(effects.OfType<PermissionCleared>()).Outcome);
        Assert.Equal(SessionPhase.Working, store.Snapshot().Single().Phase);
    }

    [Fact]
    public void Permission_prompt_notification_is_ignored_after_the_card_was_answered()
    {
        var store = new SessionStore();
        Feed(store, Payloads.Hook("PermissionRequest", Payloads.BashPermission("x")), T0, "req1");
        store.ResolvePermission("req1", T0.AddSeconds(2), PermissionOutcome.Answered);

        var effects = Feed(store, Payloads.Hook("Notification", ",\"notification_type\":\"permission_prompt\"," + PromptPermissionMessage), T0.AddSeconds(6));

        Assert.Empty(effects.OfType<NoticeShown>());
    }

    [Fact]
    public void Permission_prompt_without_a_card_becomes_needs_input()
    {
        var store = new SessionStore();

        var effects = Feed(store, Payloads.Hook("Notification", ",\"notification_type\":\"permission_prompt\"," + PromptPermissionMessage), T0);

        var notice = Assert.Single(effects.OfType<NoticeShown>());
        Assert.Equal(SessionPhase.NeedsInput, notice.Phase);
        Assert.Equal("Claude needs your permission to use Bash", notice.Text);
    }

    [Fact]
    public void Unrelated_notification_types_show_nothing()
    {
        var store = new SessionStore();

        Assert.Empty(Feed(store, Payloads.Hook("Notification", ",\"notification_type\":\"auth_success\",\"message\":\"ok\""), T0));
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public void Stop_with_stop_hook_active_is_silent()
    {
        var store = new SessionStore();

        var effects = Feed(store, Payloads.Hook("Stop", ",\"stop_hook_active\":true,\"last_assistant_message\":\"done\""), T0);

        Assert.Empty(effects.OfType<NoticeShown>());
        Assert.NotEqual(SessionPhase.Finished, store.Snapshot().Single().Phase);
    }

    [Fact]
    public void Stop_shows_the_last_message_as_finished()
    {
        var store = new SessionStore();

        var notice = Assert.Single(Feed(store, Payloads.Text("Stop.json"), T0).OfType<NoticeShown>());

        Assert.Equal(SessionPhase.Finished, notice.Phase);
        Assert.Equal("Claude Code", notice.Agent);
    }

    [Fact]
    public void Last_prompt_flows_into_the_next_card()
    {
        var store = new SessionStore();
        Feed(store, Payloads.Hook("UserPromptSubmit", ",\"prompt\":\"fix the\\nlogin bug\""), T0);

        var shown = Assert.Single(Feed(store, Payloads.Hook("PermissionRequest", Payloads.BashPermission("npm test")), T0.AddSeconds(1)).OfType<PermissionShown>());

        Assert.Equal("fix the login bug", shown.LastPrompt);
    }

    [Fact]
    public void Permission_card_carries_the_command_description()
    {
        var store = new SessionStore();
        var extra = ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"rm -rf build\",\"description\":\"Delete the build folder\"}";

        var shown = Assert.Single(Feed(store, Payloads.Hook("PermissionRequest", extra), T0).OfType<PermissionShown>());

        Assert.Equal("Delete the build folder", shown.Permission.Description);
    }

    [Fact]
    public void Session_end_removes_the_session()
    {
        var store = new SessionStore();
        Feed(store, Payloads.Hook("PermissionRequest", Payloads.BashPermission("x")), T0, "req1");

        var effects = Feed(store, Payloads.Hook("SessionEnd"), T0.AddSeconds(1));

        Assert.Contains(effects, e => e is PermissionCleared { Outcome: PermissionOutcome.Abandoned });
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Display_name_falls_back_to_the_session_title()
    {
        var store = new SessionStore();

        Feed(store, Payloads.Json(new() { ["hook_event_name"] = "SessionStart", ["session_id"] = "abcdef123456", ["session_title"] = "Refactor auth" }), T0);

        Assert.Equal("Refactor auth", store.Snapshot().Single().DisplayName);
    }
}
