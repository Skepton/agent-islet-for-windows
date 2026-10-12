using Island.Core;

namespace Island.Core.Tests;

public class CardPresenterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly RememberOption Rule = new("Always Allow (Shell)", "Allow + remember Shell(npm *)", "rule-payload");
    private static readonly RememberOption Folder = new("Allow + add folder", "Allow + add folder C:\\proj", "folder-payload");

    private static PermissionCard Permission(
        string tool = "Shell",
        string summary = "npm test",
        string? description = "Run the tests",
        string? prompt = "fix the bug",
        string? clientApp = "VS Code",
        string headline = "Agent wants to run Shell.",
        string prefix = "$",
        string? agent = "Test Agent",
        params RememberOption[] suggestions) => new()
    {
        Id = "r1",
        CreatedAt = T0,
        SessionId = "s1",
        SessionName = "my-app",
        Permission = new PendingPermission("r1", tool, summary, "default", suggestions, description, headline, prefix),
        LastPrompt = prompt,
        ClientApp = clientApp,
        Agent = agent,
    };

    [Fact]
    public void Permission_card_shows_what_the_agent_integration_supplied()
    {
        var text = CardPresenter.Present(Permission(), T0.AddSeconds(20));

        Assert.Equal("my-app · fix the bug", text.Title);
        Assert.Equal("You: fix the bug", text.YouLine);
        Assert.Equal("Agent wants to run Shell.", text.AttentionLine);
        Assert.Equal("Shell", text.ToolLabel);
        Assert.Equal("$", text.CommandPrefix);
        Assert.Equal("npm test", text.Command);
        Assert.Equal("Run the tests", text.Description);
        Assert.Equal(new[] { "Test Agent", "VS Code", "<1m" }, text.Chips);
    }

    [Fact]
    public void Missing_headline_falls_back_to_a_generic_line()
    {
        var text = CardPresenter.Present(Permission("Edit", headline: "", prefix: "", description: null), T0);

        Assert.Equal("Permission needed to use Edit.", text.AttentionLine);
        Assert.Equal("", text.CommandPrefix);
        Assert.Null(text.Description);
    }

    [Fact]
    public void Missing_prompt_client_app_and_agent_are_left_out()
    {
        var text = CardPresenter.Present(Permission(prompt: null, clientApp: null, agent: null), T0.AddMinutes(5));

        Assert.Equal("my-app", text.Title);
        Assert.Null(text.YouLine);
        Assert.Equal(new[] { "5m" }, text.Chips);
    }

    [Fact]
    public void Long_multiline_prompt_is_collapsed_and_cut()
    {
        var prompt = "first line\nsecond line " + new string('x', 300);

        var text = CardPresenter.Present(Permission(prompt: prompt), T0);

        Assert.DoesNotContain('\n', text.Title);
        Assert.True(text.Title.Length <= "my-app · ".Length + 60);
        Assert.EndsWith("…", text.Title);
        Assert.StartsWith("You: first line second line", text.YouLine);
        Assert.True(text.YouLine!.Length <= "You: ".Length + 140);
    }

    [Fact]
    public void Age_never_goes_negative_when_clocks_disagree()
    {
        var text = CardPresenter.Present(Permission(), T0.AddSeconds(-5));

        Assert.Equal("<1m", text.Chips[^1]);
    }

    [Fact]
    public void Buttons_are_No_Yes_then_one_per_suggestion()
    {
        var text = CardPresenter.Present(Permission(suggestions: new[] { Rule, Folder }), T0);

        Assert.Collection(text.Actions,
            a => Assert.Equal((PermissionActionKind.Deny, "No", -1), (a.Kind, a.Label, a.SuggestionIndex)),
            a => Assert.Equal((PermissionActionKind.Allow, "Yes", -1), (a.Kind, a.Label, a.SuggestionIndex)),
            a => Assert.Equal((PermissionActionKind.Remember, "Always Allow (Shell)", 0), (a.Kind, a.Label, a.SuggestionIndex)),
            a => Assert.Equal((PermissionActionKind.Remember, "Allow + add folder", 1), (a.Kind, a.Label, a.SuggestionIndex)));
    }

    [Fact]
    public void Without_suggestions_there_are_only_two_buttons()
    {
        Assert.Equal(2, CardPresenter.Present(Permission(), T0).Actions.Count);
    }

    [Fact]
    public void Deny_and_allow_answers_are_plain_verdicts()
    {
        var card = Permission();
        var actions = CardPresenter.Present(card, T0).Actions;

        var deny = CardPresenter.Answer(card, actions[0]);
        var allow = CardPresenter.Answer(card, actions[1]);

        Assert.IsType<DenyVerdict>(deny.Verdict);
        Assert.Equal(PermissionOutcome.Answered, deny.Outcome);
        Assert.IsType<AllowVerdict>(allow.Verdict);
        Assert.Equal(PermissionOutcome.Answered, allow.Outcome);
    }

    [Fact]
    public void Remember_answer_carries_the_chosen_option()
    {
        var card = Permission(suggestions: new[] { Rule, Folder });
        var actions = CardPresenter.Present(card, T0).Actions;

        var answer = CardPresenter.Answer(card, actions[3]);

        Assert.Equal(Folder, Assert.IsType<RememberVerdict>(answer.Verdict).Option);
        Assert.Contains(Folder.Detail, answer.Log);
    }

    [Fact]
    public void Answer_in_terminal_sends_no_decision()
    {
        var answer = CardPresenter.AnswerInTerminal(Permission());

        Assert.Null(answer.Verdict);
        Assert.Equal(PermissionOutcome.DeferredToTerminal, answer.Outcome);
    }

    [Fact]
    public void Strip_lists_5h_before_7d_with_rounded_percent_and_countdown()
    {
        var snapshot = new UsageSnapshot(T0, new[]
        {
            new UsageWindow(UsageWindowId.SevenDay, 55.4, T0.AddDays(2).AddHours(3)),
            new UsageWindow(UsageWindowId.FiveHour, 71.6, T0.AddHours(1).AddMinutes(10)),
        });

        var strip = CardPresenter.PresentStrip(snapshot, T0);

        Assert.Equal(new UsageStripItem("5h", "72%", 1, "1h 10m"), strip[0]);
        Assert.Equal(new UsageStripItem("7d", "55%", 0, "2d"), strip[1]);
    }

    [Fact]
    public void Finished_notice_is_marked_done()
    {
        var card = new NoticeCard
        {
            Id = "notice:s1", CreatedAt = T0, SessionId = "s1", SessionName = "my-app",
            Phase = SessionPhase.Finished, Text = "All done", LastPrompt = "ship it", ClientApp = "Terminal", Agent = "Test Agent",
        };

        var text = CardPresenter.Present(card, T0.AddHours(2));

        Assert.True(text.IsDone);
        Assert.Equal("All done", text.Body);
        Assert.Equal("my-app · ship it", text.Title);
        Assert.Equal(new[] { "Test Agent", "Terminal", "2h" }, text.Chips);
    }

    [Fact]
    public void Needs_input_notice_is_not_done()
    {
        var card = new NoticeCard
        {
            Id = "notice:s1", CreatedAt = T0, SessionId = "s1", SessionName = "my-app",
            Phase = SessionPhase.NeedsInput, Text = "Claude is waiting for your input",
        };

        var text = CardPresenter.Present(card, T0);

        Assert.False(text.IsDone);
        Assert.Null(text.YouLine);
    }

    [Theory]
    [InlineData(UsageWindowId.FiveHour, 35, 36.2, 0, "5h limit  ·  35 % used", "resets 15:10")]
    [InlineData(UsageWindowId.SevenDay, 70, 71, 1, "7d limit  ·  70 % used", "resets Wed 15:10")]
    [InlineData(UsageWindowId.FiveHour, 90, 100.7, 2, "5h limit  ·  90 % used", "resets 15:10")]
    public void Usage_card_shows_title_level_and_reset_time(UsageWindowId window, int threshold, double percent, int level, string title, string resets)
    {
        var card = new UsageCard
        {
            Id = "u", CreatedAt = T0,
            Milestone = new MilestoneEvent(window, threshold, percent, new DateTimeOffset(2026, 10, 14, 12, 10, 0, TimeSpan.Zero)),
        };

        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        UsageCardText text;
        try
        {
            text = CardPresenter.Present(card, TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(3), "test", "test"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }

        Assert.Equal(title, text.Title);
        Assert.Equal(level, text.Level);
        Assert.Equal(Math.Min(percent, 100), text.Percent);
        Assert.Equal(resets, text.ResetsText);
    }
}
