using Island.Core;

namespace Island.Core.Tests;

public class OverlayStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static PendingPermission Pending(string id, string tool = "Bash") =>
        new(id, tool, "npm test", "default", Array.Empty<RememberOption>(), "Run the test suite");

    private static PermissionShown Permission(string id, string session = "s1") =>
        new(session, "my-app", Pending(id), "fix the bug", "VS Code", "Test Agent");

    private static NoticeShown Needs(string session = "s1", string text = "Claude is waiting") =>
        new(session, "my-app", SessionPhase.NeedsInput, text);

    private static NoticeShown Finished(string session = "s1", string text = "All done") =>
        new(session, "my-app", SessionPhase.Finished, text);

    private static UsageUpdate Milestone(UsageWindowId window, int threshold, double percent = 0)
    {
        var milestone = new MilestoneEvent(window, threshold, percent == 0 ? threshold + 1 : percent, T0.AddHours(3));
        return new UsageUpdate(new UsageFetchResult(UsageFetchStatus.Ok), new[] { milestone }, null, 0);
    }

    private static string[] Ids(OverlaySnapshot snapshot) => snapshot.Cards.Select(c => c.Id).ToArray();

    [Fact]
    public void Starts_empty()
    {
        var snapshot = new OverlayState().Snapshot();

        Assert.Null(snapshot.Current);
        Assert.Empty(snapshot.Cards);
        Assert.Equal(-1, snapshot.Index);
    }

    [Fact]
    public void Permission_request_becomes_a_card_carrying_its_context()
    {
        var state = new OverlayState();

        state.Apply(new StoreEffect[] { Permission("r1") }, T0);

        var card = Assert.IsType<PermissionCard>(state.Snapshot().Current);
        Assert.Equal("r1", card.Id);
        Assert.Equal("my-app", card.SessionName);
        Assert.Equal("fix the bug", card.LastPrompt);
        Assert.Equal("VS Code", card.ClientApp);
        Assert.Equal("Test Agent", card.Agent);
        Assert.Equal("Run the test suite", card.Permission.Description);
        Assert.Null(card.ExpiresAt);
    }

    [Fact]
    public void Cards_are_ordered_by_priority_then_age()
    {
        var state = new OverlayState();
        state.Apply(new StoreEffect[] { Finished("a"), Needs("b") }, T0);
        state.ApplyUsage(Milestone(UsageWindowId.FiveHour, 25), T0.AddSeconds(1));
        state.Apply(new StoreEffect[] { Permission("late", "c") }, T0.AddSeconds(2));
        state.Apply(new StoreEffect[] { Permission("early", "d") }, T0.AddSeconds(1));

        var order = state.Snapshot().Cards.Select(c => c.Kind).ToArray();

        Assert.Equal(new[] { CardKind.Permission, CardKind.Permission, CardKind.NeedsInput, CardKind.UsageMilestone, CardKind.Finished }, order);
        Assert.Equal(new[] { "early", "late" }, state.Snapshot().Cards.Take(2).Select(c => c.Id).ToArray()); // same priority: oldest first
    }

    [Fact]
    public void Answering_removes_the_card()
    {
        var state = new OverlayState();
        state.Apply(new StoreEffect[] { Permission("r1") }, T0);

        state.Apply(new StoreEffect[] { new PermissionCleared("s1", "r1", PermissionOutcome.Answered) }, T0.AddSeconds(2));

        Assert.Empty(state.Snapshot().Cards);
    }

    [Fact]
    public void A_new_notice_replaces_the_old_one_for_the_same_session()
    {
        var state = new OverlayState();
        state.Apply(new StoreEffect[] { Needs("s1", "first") }, T0);

        state.Apply(new StoreEffect[] { Needs("s1", "second"), Needs("s2", "other") }, T0.AddSeconds(1));

        var texts = state.Snapshot().Cards.OfType<NoticeCard>().Select(n => n.Text).OrderBy(t => t).ToArray();
        Assert.Equal(new[] { "other", "second" }, texts);
    }

    [Fact]
    public void Clearing_a_notice_or_removing_a_session_drops_its_cards()
    {
        var state = new OverlayState();
        state.Apply(new StoreEffect[] { Needs("s1"), Permission("r1", "s2"), Needs("s3") }, T0);

        state.Apply(new StoreEffect[] { new NoticeCleared("s1"), new SessionRemoved("s2") }, T0.AddSeconds(1));

        Assert.Equal(new[] { "notice:s3" }, Ids(state.Snapshot()));
    }

    [Fact]
    public void Finished_notices_expire_but_needs_input_notices_stay()
    {
        var state = new OverlayState();
        state.Apply(new StoreEffect[] { Finished("s1"), Needs("s2") }, T0);

        Assert.False(state.Tick(T0.AddSeconds(5)));
        Assert.True(state.Tick(T0.AddSeconds(7)));

        Assert.Equal(new[] { "notice:s2" }, Ids(state.Snapshot()));
        Assert.False(state.Tick(T0.AddHours(5)));
    }

    [Fact]
    public void Permission_cards_never_expire()
    {
        var state = new OverlayState();
        state.Apply(new StoreEffect[] { Permission("r1") }, T0);

        Assert.False(state.Tick(T0.AddDays(2)));
        Assert.Single(state.Snapshot().Cards);
    }

    [Fact]
    public void Finished_cards_can_be_turned_off()
    {
        var state = new OverlayState(new OverlayOptions { ShowFinished = false });

        state.Apply(new StoreEffect[] { Finished() }, T0);

        Assert.Empty(state.Snapshot().Cards);
    }

    [Fact]
    public void Milestones_expire_after_a_few_seconds_unless_usage_is_high()
    {
        var state = new OverlayState();
        state.ApplyUsage(Milestone(UsageWindowId.FiveHour, 35), T0);
        state.ApplyUsage(Milestone(UsageWindowId.SevenDay, 90), T0);

        state.Tick(T0.AddSeconds(9));

        var remaining = Assert.Single(state.Snapshot().Cards.OfType<UsageCard>());
        Assert.Equal(UsageWindowId.SevenDay, remaining.Milestone.Window);
        Assert.True(remaining.Sticky);
    }

    [Fact]
    public void Only_the_latest_milestone_per_window_is_kept()
    {
        var state = new OverlayState();
        state.ApplyUsage(Milestone(UsageWindowId.FiveHour, 25), T0);
        state.ApplyUsage(Milestone(UsageWindowId.FiveHour, 30), T0.AddSeconds(1));

        var card = Assert.Single(state.Snapshot().Cards.OfType<UsageCard>());

        Assert.Equal(30, card.Milestone.Threshold);
    }

    [Fact]
    public void Updates_without_milestones_change_nothing()
    {
        var state = new OverlayState();

        state.ApplyUsage(new UsageUpdate(new UsageFetchResult(UsageFetchStatus.Ok), Array.Empty<MilestoneEvent>(), null, 0), T0);

        Assert.Empty(state.Snapshot().Cards);
    }

    [Fact]
    public void Full_screen_hides_only_the_non_critical_cards()
    {
        var state = new OverlayState();
        state.Apply(new StoreEffect[] { Finished("s1"), Needs("s2"), Permission("r1", "s3") }, T0);
        state.ApplyUsage(Milestone(UsageWindowId.FiveHour, 25), T0);

        state.SuppressNonCritical = true;
        var hidden = state.Snapshot().Cards.Select(c => c.Kind).ToArray();
        state.SuppressNonCritical = false;
        var shown = state.Snapshot().Cards.Select(c => c.Kind).ToArray();

        Assert.Equal(new[] { CardKind.Permission, CardKind.NeedsInput }, hidden);
        Assert.Equal(4, shown.Length);
    }

    [Fact]
    public void Next_and_previous_step_through_the_cards_and_wrap()
    {
        var state = new OverlayState();
        state.Apply(new StoreEffect[] { Permission("r1", "s1"), Needs("s2"), Finished("s3") }, T0);

        Assert.Equal(0, state.Snapshot().Index);
        state.Next();
        Assert.Equal("notice:s2", state.Snapshot().Current!.Id);
        state.Next();
        state.Next();
        Assert.Equal("r1", state.Snapshot().Current!.Id); // wrapped
        state.Previous();
        Assert.Equal("notice:s3", state.Snapshot().Current!.Id);
    }

    [Fact]
    public void A_new_permission_request_pulls_the_front_even_while_browsing()
    {
        var state = new OverlayState();
        state.Apply(new StoreEffect[] { Permission("r1", "s1"), Needs("s2") }, T0);
        state.Next();

        state.Apply(new StoreEffect[] { Permission("r2", "s3") }, T0.AddSeconds(5));

        Assert.Equal(0, state.Snapshot().Index);
        Assert.IsType<PermissionCard>(state.Snapshot().Current);
    }

    [Fact]
    public void Removing_the_selected_card_falls_back_to_the_top()
    {
        var state = new OverlayState();
        state.Apply(new StoreEffect[] { Permission("r1", "s1"), Needs("s2") }, T0);
        state.Next();

        state.Dismiss("notice:s2");

        Assert.Equal("r1", state.Snapshot().Current!.Id);
    }

    [Fact]
    public void Stepping_with_a_single_card_does_nothing()
    {
        var state = new OverlayState();
        state.Apply(new StoreEffect[] { Needs("s1") }, T0);

        state.Next();
        state.Previous();

        Assert.Equal(0, state.Snapshot().Index);
    }
}

public class IslandCollapseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static OverlaySnapshot With(StoreEffect effect)
    {
        var state = new OverlayState();
        state.Apply(new[] { effect }, T0);
        return state.Snapshot();
    }

    private static readonly PermissionShown Permission =
        new("s1", "my-app", new PendingPermission("r1", "Bash", "npm test", "default", Array.Empty<RememberOption>()));

    [Fact]
    public void A_waiting_card_stays_open_long_enough_to_read_then_collapses_unless_the_pointer_is_over_it()
    {
        var snapshot = With(Permission);

        Assert.False(IslandCollapse.ShouldCollapse(snapshot, T0, T0.AddSeconds(19), pointerOver: false));
        Assert.True(IslandCollapse.ShouldCollapse(snapshot, T0, T0.AddSeconds(20), pointerOver: false));
        Assert.False(IslandCollapse.ShouldCollapse(snapshot, T0, T0.AddMinutes(5), pointerOver: true));
    }

    [Fact]
    public void Questions_and_new_projects_collapse_too_but_finished_cards_never_do()
    {
        var question = With(new NoticeShown("s1", "my-app", SessionPhase.NeedsInput, "Which one?"));
        var project = With(new ProjectApprovalRequested(ProjectRef.From(@"C:\p")!));
        var finished = With(new NoticeShown("s1", "my-app", SessionPhase.Finished, "Done"));

        var later = T0.AddSeconds(25);

        Assert.True(IslandCollapse.ShouldCollapse(question, T0, later, false));
        Assert.True(IslandCollapse.ShouldCollapse(project, T0, later, false));
        Assert.False(IslandCollapse.ShouldCollapse(finished, T0, later, false));
        Assert.False(IslandCollapse.ShouldCollapse(new OverlayState().Snapshot(), T0, later, false));
    }

    [Fact]
    public void The_island_offers_one_remember_button_and_the_panel_all_of_them()
    {
        var options = new[]
        {
            new RememberOption("Always Allow (PowerShell)", "Allow + remember PowerShell(Start-Sleep:*) in this project (just you)", "a"),
            new RememberOption("Always Allow (PowerShell)", "Allow + remember PowerShell(Start-Sleep:*) in all projects", "b"),
        };
        var card = new PermissionCard
        {
            Id = "r1", CreatedAt = T0, SessionId = "s1", SessionName = "my-app",
            Permission = new PendingPermission("r1", "PowerShell", "Start-Sleep 10", "default", options),
        };

        Assert.Equal(3, CardPresenter.Present(card, T0, maxRemember: 1).Actions.Count);
        Assert.Equal(4, CardPresenter.Present(card, T0).Actions.Count);
    }
}

public class UsageFormatTests
{
    [Theory]
    [InlineData(0, "now")]
    [InlineData(-30, "now")]
    [InlineData(20, "1m")]
    [InlineData(45 * 60, "45m")]
    [InlineData(70 * 60, "1h 10m")]
    [InlineData(26 * 3600, "1d")]
    [InlineData(2 * 86400 + 5 * 3600, "2d")]
    public void Countdown_reads_like_the_reference(int seconds, string expected)
    {
        Assert.Equal(expected, UsageFormat.Countdown(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(10, "<1m")]
    [InlineData(5 * 60, "5m")]
    [InlineData(3 * 3600, "3h")]
    [InlineData(2 * 86400, "2d")]
    public void Age_is_short_and_coarse(int seconds, string expected)
    {
        Assert.Equal(expected, UsageFormat.Age(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(69.9, 0)]
    [InlineData(70, 1)]
    [InlineData(89.9, 1)]
    [InlineData(90, 2)]
    [InlineData(100, 2)]
    public void Level_goes_green_amber_red(double percent, int expected)
    {
        Assert.Equal(expected, UsageFormat.Level(percent));
    }

    [Fact]
    public void Window_labels_match_the_reference()
    {
        Assert.Equal("5h", UsageFormat.WindowLabel(UsageWindowId.FiveHour));
        Assert.Equal("7d", UsageFormat.WindowLabel(UsageWindowId.SevenDay));
    }
}
