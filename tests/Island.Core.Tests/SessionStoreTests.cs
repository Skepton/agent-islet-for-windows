using System.Text.Json;
using Island.Core;

namespace Island.Core.Tests;

public class SessionStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private const string PromptPermissionMessage = "\"message\":\"Claude needs your permission to use Bash\"";

    [Fact]
    public void Permission_request_shows_a_card_with_its_summary()
    {
        var store = new SessionStore();

        var effects = store.Apply(Payloads.Hook("PermissionRequest", Payloads.BashPermission("npm test")), "req1", T0);

        var shown = Assert.Single(effects.OfType<PermissionShown>());
        Assert.Equal("npm test", shown.Permission.Summary);
        Assert.Equal("Bash", shown.Permission.ToolName);
        Assert.Equal("my-app", shown.DisplayName);
        Assert.Equal(SessionPhase.NeedsPermission, store.Snapshot().Single().Phase);
    }

    [Fact]
    public void Tool_running_after_a_permission_resolves_the_card_elsewhere()
    {
        var store = new SessionStore();
        store.Apply(Payloads.Hook("PermissionRequest", Payloads.BashPermission("x")), "req1", T0);

        var effects = store.Apply(Payloads.Hook("PostToolUse", ",\"tool_name\":\"Bash\""), "ignored", T0.AddSeconds(3));

        var cleared = Assert.Single(effects.OfType<PermissionCleared>());
        Assert.Equal("req1", cleared.RequestId);
        Assert.Equal(PermissionOutcome.ResolvedElsewhere, cleared.Outcome);
        Assert.Equal(SessionPhase.Working, store.Snapshot().Single().Phase);
    }

    [Fact]
    public void Permission_prompt_notification_is_ignored_after_the_card_was_answered()
    {
        var store = new SessionStore();
        store.Apply(Payloads.Hook("PermissionRequest", Payloads.BashPermission("x")), "req1", T0);
        store.ResolvePermission("req1", T0.AddSeconds(2), PermissionOutcome.Answered);

        var effects = store.Apply(
            Payloads.Hook("Notification", ",\"notification_type\":\"permission_prompt\"," + PromptPermissionMessage),
            "n1",
            T0.AddSeconds(6));

        Assert.Empty(effects.OfType<NoticeShown>());
    }

    [Fact]
    public void Permission_prompt_notification_is_ignored_while_the_card_is_still_showing()
    {
        var store = new SessionStore();
        store.Apply(Payloads.Hook("PermissionRequest", Payloads.BashPermission("x")), "req1", T0);

        var effects = store.Apply(
            Payloads.Hook("Notification", ",\"notification_type\":\"permission_prompt\"," + PromptPermissionMessage),
            "n1",
            T0.AddSeconds(6));

        Assert.Empty(effects.OfType<NoticeShown>());
        Assert.NotNull(store.Snapshot().Single().Pending);
    }

    [Fact]
    public void Permission_prompt_without_a_card_becomes_needs_input()
    {
        var store = new SessionStore();

        var effects = store.Apply(
            Payloads.Hook("Notification", ",\"notification_type\":\"permission_prompt\"," + PromptPermissionMessage),
            "n1",
            T0);

        var notice = Assert.Single(effects.OfType<NoticeShown>());
        Assert.Equal(SessionPhase.NeedsInput, notice.Phase);
        Assert.Equal("Claude needs your permission to use Bash", notice.Text);
    }

    [Fact]
    public void Idle_prompt_notification_becomes_needs_input()
    {
        var store = new SessionStore();

        var effects = store.Apply(
            Payloads.Hook("Notification", ",\"notification_type\":\"idle_prompt\",\"message\":\"Waiting\""),
            "n1",
            T0);

        Assert.Equal(SessionPhase.NeedsInput, Assert.Single(effects.OfType<NoticeShown>()).Phase);
    }

    [Fact]
    public void Unrelated_notification_types_are_ignored()
    {
        var store = new SessionStore();

        var effects = store.Apply(
            Payloads.Hook("Notification", ",\"notification_type\":\"auth_success\",\"message\":\"ok\""),
            "n1",
            T0);

        Assert.Empty(effects);
    }

    [Fact]
    public void Stop_with_stop_hook_active_is_silent()
    {
        var store = new SessionStore();

        var effects = store.Apply(
            Payloads.Hook("Stop", ",\"stop_hook_active\":true,\"last_assistant_message\":\"done\""),
            "s1",
            T0);

        Assert.Empty(effects.OfType<NoticeShown>());
        Assert.NotEqual(SessionPhase.Finished, store.Snapshot().Single().Phase);
    }

    [Fact]
    public void Stop_shows_a_finished_notice_truncated_to_400_characters()
    {
        var store = new SessionStore();
        var longMessage = new string('w', 900);

        var effects = store.Apply(
            Payloads.Hook("Stop", ",\"last_assistant_message\":\"" + longMessage + "\""),
            "s1",
            T0);

        var notice = Assert.Single(effects.OfType<NoticeShown>());
        Assert.Equal(SessionPhase.Finished, notice.Phase);
        Assert.True(notice.Text.Length <= 400);
        Assert.EndsWith("…", notice.Text);
    }

    [Fact]
    public void Last_prompt_and_client_app_flow_into_the_cards()
    {
        var store = new SessionStore();
        store.Apply(Payloads.Hook("UserPromptSubmit", ",\"prompt\":\"fix the\\nlogin bug\"") with { ClientApp = "VS Code" }, "p", T0);

        var shown = Assert.Single(
            store.Apply(Payloads.Hook("PermissionRequest", Payloads.BashPermission("npm test")), "req1", T0.AddSeconds(1)).OfType<PermissionShown>());

        Assert.Equal("fix the login bug", shown.LastPrompt);
        Assert.Equal("VS Code", shown.ClientApp); // remembered from the earlier event
    }

    [Fact]
    public void Last_prompt_is_truncated_and_lives_only_in_memory()
    {
        var store = new SessionStore();

        store.Apply(Payloads.Hook("UserPromptSubmit", ",\"prompt\":\"" + new string('p', 500) + "\""), "p", T0);

        Assert.True(store.Snapshot().Single().LastPrompt!.Length <= 160);
    }

    [Fact]
    public void Permission_card_carries_the_command_description()
    {
        var store = new SessionStore();
        var extra = ",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"rm -rf build\",\"description\":\"Delete the build folder\"}";

        var shown = Assert.Single(store.Apply(Payloads.Hook("PermissionRequest", extra), "req1", T0).OfType<PermissionShown>());

        Assert.Equal("Delete the build folder", shown.Permission.Description);
    }

    [Fact]
    public void Newer_permission_request_supersedes_the_older_one()
    {
        var store = new SessionStore();
        store.Apply(Payloads.Hook("PermissionRequest", Payloads.BashPermission("first")), "req1", T0);

        var effects = store.Apply(Payloads.Hook("PermissionRequest", Payloads.BashPermission("second")), "req2", T0.AddSeconds(1));

        Assert.Contains(effects.OfType<PermissionCleared>(), c => c.RequestId == "req1" && c.Outcome == PermissionOutcome.Superseded);
        Assert.Equal("req2", store.Snapshot().Single().Pending!.RequestId);
    }

    [Fact]
    public void Session_end_abandons_the_pending_card_and_removes_the_session()
    {
        var store = new SessionStore();
        store.Apply(Payloads.Hook("PermissionRequest", Payloads.BashPermission("x")), "req1", T0);

        var effects = store.Apply(Payloads.Hook("SessionEnd"), "end", T0.AddSeconds(1));

        Assert.Contains(effects, e => e is PermissionCleared { Outcome: PermissionOutcome.Abandoned });
        Assert.Single(effects.OfType<SessionRemoved>());
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Client_disconnect_abandons_the_card()
    {
        var store = new SessionStore();
        store.Apply(Payloads.Hook("PermissionRequest", Payloads.BashPermission("x")), "req1", T0);

        var effects = store.ResolvePermission("req1", T0.AddSeconds(1), PermissionOutcome.Abandoned);

        Assert.Equal(PermissionOutcome.Abandoned, Assert.Single(effects.OfType<PermissionCleared>()).Outcome);
        Assert.Null(store.Snapshot().Single().Pending);
    }

    [Fact]
    public void Resolving_an_unknown_request_has_no_effect()
    {
        var store = new SessionStore();

        Assert.Empty(store.ResolvePermission("nope", T0, PermissionOutcome.Answered));
    }

    [Theory]
    [InlineData("C:\\Users\\me\\proj\\", "proj")]
    [InlineData("/home/me/other", "other")]
    public void Display_name_is_the_folder_name(string cwd, string expected)
    {
        var store = new SessionStore();
        var hook = Payloads.Parse(new Dictionary<string, string?> { ["hook_event_name"] = "SessionStart", ["session_id"] = "abcdef123456", ["cwd"] = cwd });

        store.Apply(hook, "x", T0);

        Assert.Equal(expected, store.Snapshot().Single().DisplayName);
    }

    [Fact]
    public void Display_name_falls_back_to_the_session_title_then_the_id()
    {
        var withTitle = Payloads.Parse(new Dictionary<string, string?> { ["hook_event_name"] = "SessionStart", ["session_id"] = "abcdef123456", ["session_title"] = "Refactor auth" });
        var bare = Payloads.Parse(new Dictionary<string, string?> { ["hook_event_name"] = "SessionStart", ["session_id"] = "abcdef123456" });

        var store = new SessionStore();
        store.Apply(withTitle, "x", T0);
        store.Apply(bare, "y", T0);

        Assert.Equal("abcdef12", store.Snapshot().Single().DisplayName);
    }

    [Fact]
    public void Prune_removes_sessions_idle_for_more_than_twelve_hours()
    {
        var store = new SessionStore();
        store.Apply(Payloads.Hook("SessionStart"), "x", T0);

        var effects = store.Prune(T0.AddHours(13));

        Assert.Single(effects.OfType<SessionRemoved>());
        Assert.Empty(store.Snapshot());
    }
}
