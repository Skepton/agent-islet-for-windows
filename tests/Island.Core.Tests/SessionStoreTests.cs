using Island.Core;

namespace Island.Core.Tests;

/// <summary>The state machine on its own, fed neutral events. Claude Code's mapping onto it is tested in Island.ClaudeCode.Tests.</summary>
public class SessionStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Permission_request_shows_a_card_with_its_summary()
    {
        var store = new SessionStore();

        var effects = store.Apply(Events.Permission("req1", "npm test"), T0);

        var shown = Assert.Single(effects.OfType<PermissionShown>());
        Assert.Equal("npm test", shown.Permission.Summary);
        Assert.Equal("req1", shown.Permission.RequestId);
        Assert.Equal("my-app", shown.DisplayName);
        Assert.Equal(SessionPhase.NeedsPermission, store.Snapshot().Single().Phase);
    }

    [Fact]
    public void Tool_proceeding_resolves_the_card_elsewhere()
    {
        var store = new SessionStore();
        store.Apply(Events.Permission("req1"), T0);

        var effects = store.Apply(Events.Proceeded(), T0.AddSeconds(3));

        var cleared = Assert.Single(effects.OfType<PermissionCleared>());
        Assert.Equal("req1", cleared.RequestId);
        Assert.Equal(PermissionOutcome.ResolvedElsewhere, cleared.Outcome);
        Assert.Equal(SessionPhase.Working, store.Snapshot().Single().Phase);
    }

    [Fact]
    public void Echo_of_a_permission_is_dropped_while_the_card_shows_or_was_just_answered()
    {
        var showing = new SessionStore();
        showing.Apply(Events.Permission("req1"), T0);
        var answered = new SessionStore();
        answered.Apply(Events.Permission("req1"), T0);
        answered.ResolvePermission("req1", T0.AddSeconds(2), PermissionOutcome.Answered);

        Assert.Empty(showing.Apply(Events.Input("needs permission", echoesPermission: true), T0.AddSeconds(6)).OfType<NoticeShown>());
        Assert.Empty(answered.Apply(Events.Input("needs permission", echoesPermission: true), T0.AddSeconds(6)).OfType<NoticeShown>());
        Assert.NotNull(showing.Snapshot().Single().Pending);
    }

    [Fact]
    public void Echo_of_a_permission_long_after_the_card_becomes_needs_input()
    {
        var store = new SessionStore();
        store.Apply(Events.Permission("req1"), T0);
        store.ResolvePermission("req1", T0.AddSeconds(1), PermissionOutcome.Answered);

        var effects = store.Apply(Events.Input("needs permission", echoesPermission: true), T0.AddMinutes(5));

        Assert.Equal(SessionPhase.NeedsInput, Assert.Single(effects.OfType<NoticeShown>()).Phase);
    }

    [Fact]
    public void Input_request_becomes_needs_input_truncated_to_160_characters()
    {
        var store = new SessionStore();

        var notice = Assert.Single(store.Apply(Events.Input(new string('q', 500)), T0).OfType<NoticeShown>());

        Assert.Equal(SessionPhase.NeedsInput, notice.Phase);
        Assert.True(notice.Text.Length <= 160);
    }

    [Fact]
    public void A_continuing_turn_shows_nothing_but_releases_the_card()
    {
        var store = new SessionStore();
        store.Apply(Events.Permission("req1"), T0);

        var effects = store.Apply(Events.Finished("done", continuing: true), T0.AddSeconds(1));

        Assert.Empty(effects.OfType<NoticeShown>());
        Assert.Single(effects.OfType<PermissionCleared>());
        Assert.NotEqual(SessionPhase.Finished, store.Snapshot().Single().Phase);
    }

    [Fact]
    public void Finished_turn_shows_a_notice_truncated_to_400_characters()
    {
        var store = new SessionStore();

        var notice = Assert.Single(store.Apply(Events.Finished(new string('w', 900)), T0).OfType<NoticeShown>());

        Assert.Equal(SessionPhase.Finished, notice.Phase);
        Assert.True(notice.Text.Length <= 400);
        Assert.EndsWith("…", notice.Text);
    }

    [Fact]
    public void Prompt_client_app_and_agent_are_remembered_for_later_cards()
    {
        var store = new SessionStore();
        store.Apply(Events.Prompt("fix the\nlogin bug") with { ClientApp = "VS Code", Agent = "Test Agent" }, T0);

        var shown = Assert.Single(store.Apply(Events.Permission("req1"), T0.AddSeconds(1)).OfType<PermissionShown>());

        Assert.Equal("fix the login bug", shown.LastPrompt);
        Assert.Equal("VS Code", shown.ClientApp);
        Assert.Equal("Test Agent", shown.Agent);
    }

    [Fact]
    public void Last_prompt_is_truncated_and_lives_only_in_memory()
    {
        var store = new SessionStore();

        store.Apply(Events.Prompt(new string('p', 500)), T0);

        Assert.True(store.Snapshot().Single().LastPrompt!.Length <= 160);
    }

    [Fact]
    public void Newer_permission_request_supersedes_the_older_one()
    {
        var store = new SessionStore();
        store.Apply(Events.Permission("req1", "first"), T0);

        var effects = store.Apply(Events.Permission("req2", "second"), T0.AddSeconds(1));

        Assert.Contains(effects.OfType<PermissionCleared>(), c => c.RequestId == "req1" && c.Outcome == PermissionOutcome.Superseded);
        Assert.Equal("req2", store.Snapshot().Single().Pending!.RequestId);
    }

    [Fact]
    public void Session_end_abandons_the_pending_card_and_removes_the_session()
    {
        var store = new SessionStore();
        store.Apply(Events.Permission("req1"), T0);

        var effects = store.Apply(Events.Ended(), T0.AddSeconds(1));

        Assert.Contains(effects, e => e is PermissionCleared { Outcome: PermissionOutcome.Abandoned });
        Assert.Single(effects.OfType<SessionRemoved>());
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void Activity_keeps_a_session_alive_without_showing_anything()
    {
        var store = new SessionStore();

        var effects = store.Apply(new SessionActivity { SessionId = "s1", Cwd = Events.Cwd }, T0);

        Assert.Empty(effects);
        Assert.Equal(T0, store.Snapshot().Single().LastSeen);
    }

    [Fact]
    public void Client_disconnect_abandons_the_card()
    {
        var store = new SessionStore();
        store.Apply(Events.Permission("req1"), T0);

        var effects = store.ResolvePermission("req1", T0.AddSeconds(1), PermissionOutcome.Abandoned);

        Assert.Equal(PermissionOutcome.Abandoned, Assert.Single(effects.OfType<PermissionCleared>()).Outcome);
        Assert.Null(store.Snapshot().Single().Pending);
    }

    [Fact]
    public void Resolving_an_unknown_request_has_no_effect()
    {
        Assert.Empty(new SessionStore().ResolvePermission("nope", T0, PermissionOutcome.Answered));
    }

    [Theory]
    [InlineData("C:\\Users\\me\\proj\\", "proj")]
    [InlineData("/home/me/other", "other")]
    public void Display_name_is_the_folder_name(string cwd, string expected)
    {
        var store = new SessionStore();

        store.Apply(new SessionStarted { SessionId = "abcdef123456", Cwd = cwd }, T0);

        Assert.Equal(expected, store.Snapshot().Single().DisplayName);
    }

    [Fact]
    public void Display_name_falls_back_to_the_session_title_then_the_id()
    {
        var titled = new SessionStore();
        titled.Apply(new SessionStarted { SessionId = "abcdef123456", SessionTitle = "Refactor auth" }, T0);
        var bare = new SessionStore();
        bare.Apply(new SessionStarted { SessionId = "abcdef123456" }, T0);

        Assert.Equal("Refactor auth", titled.Snapshot().Single().DisplayName);
        Assert.Equal("abcdef12", bare.Snapshot().Single().DisplayName);
    }

    [Fact]
    public void Prune_removes_sessions_idle_for_more_than_twelve_hours()
    {
        var store = new SessionStore();
        store.Apply(Events.Started(), T0);

        var effects = store.Prune(T0.AddHours(13));

        Assert.Single(effects.OfType<SessionRemoved>());
        Assert.Empty(store.Snapshot());
    }
}
