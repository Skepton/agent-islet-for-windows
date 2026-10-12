using Island.Core;

namespace Island.Core.Tests;

public class CardPresenterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private const string AddRules =
        """{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"node -e ' *"}],"behavior":"allow","destination":"localSettings"}""";

    private const string AddDirectories =
        """{"type":"addDirectories","directories":["C:\\proj"],"destination":"session"}""";

    private static PermissionCard Permission(
        string tool = "Bash",
        string summary = "npm test",
        string? description = "Run the tests",
        string? prompt = "fix the bug",
        string? clientApp = "VS Code",
        params string[] suggestions) => new()
    {
        Id = "r1",
        CreatedAt = T0,
        SessionId = "s1",
        SessionName = "my-app",
        Permission = new PendingPermission("r1", tool, summary, "default", SuggestionChoice.FromRaw(suggestions), description),
        LastPrompt = prompt,
        ClientApp = clientApp,
    };

    [Fact]
    public void Bash_permission_reads_like_the_reference_card()
    {
        var text = CardPresenter.Present(Permission(), T0.AddSeconds(20));

        Assert.Equal("my-app · fix the bug", text.Title);
        Assert.Equal("You: fix the bug", text.YouLine);
        Assert.Equal("Claude wants to run Bash.", text.AttentionLine);
        Assert.Equal("Bash", text.ToolLabel);
        Assert.Equal("$", text.CommandPrefix);
        Assert.Equal("npm test", text.Command);
        Assert.Equal("Run the tests", text.Description);
        Assert.Equal(new[] { "Claude Code", "VS Code", "<1m" }, text.Chips);
    }

    [Fact]
    public void PowerShell_uses_its_own_prefix_and_wording()
    {
        var text = CardPresenter.Present(Permission("PowerShell"), T0);

        Assert.Equal("PS>", text.CommandPrefix);
        Assert.Equal("Claude wants to run PowerShell.", text.AttentionLine);
    }

    [Fact]
    public void Non_shell_tools_have_no_prefix_and_say_use()
    {
        var text = CardPresenter.Present(Permission("Edit", "src/a.cs", description: null), T0);

        Assert.Equal("", text.CommandPrefix);
        Assert.Equal("Claude wants to use Edit.", text.AttentionLine);
        Assert.Null(text.Description);
    }

    [Fact]
    public void Missing_prompt_and_client_app_are_left_out()
    {
        var text = CardPresenter.Present(Permission(prompt: null, clientApp: null), T0.AddMinutes(5));

        Assert.Equal("my-app", text.Title);
        Assert.Null(text.YouLine);
        Assert.Equal(new[] { "Claude Code", "5m" }, text.Chips);
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
        var text = CardPresenter.Present(Permission(suggestions: new[] { AddRules, AddDirectories }), T0);

        Assert.Collection(text.Actions,
            a => Assert.Equal((PermissionActionKind.Deny, "No", -1), (a.Kind, a.Label, a.SuggestionIndex)),
            a => Assert.Equal((PermissionActionKind.Allow, "Yes", -1), (a.Kind, a.Label, a.SuggestionIndex)),
            a => Assert.Equal((PermissionActionKind.Remember, "Always Allow (Bash)", 0), (a.Kind, a.Label, a.SuggestionIndex)),
            a => Assert.Equal((PermissionActionKind.Remember, "Allow + add folder", 1), (a.Kind, a.Label, a.SuggestionIndex)));
    }

    [Fact]
    public void Without_suggestions_there_are_only_two_buttons()
    {
        Assert.Equal(2, CardPresenter.Present(Permission(), T0).Actions.Count);
    }

    [Fact]
    public void Always_allow_names_the_rule_tool_and_falls_back_to_the_permission_tool()
    {
        var mcp = SuggestionChoice.FromRaw(new[] { """{"type":"addRules","rules":[{"toolName":"mcp__x__y"}]}""" })[0];
        var noRules = SuggestionChoice.FromRaw(new[] { """{"type":"addRules"}""" })[0];

        Assert.Equal("Always Allow (mcp__x__y)", CardPresenter.SuggestionLabel(mcp, "Bash"));
        Assert.Equal("Always Allow (WebFetch)", CardPresenter.SuggestionLabel(noRules, "WebFetch"));
    }

    [Fact]
    public void Unknown_suggestion_types_keep_the_store_label()
    {
        var choice = SuggestionChoice.FromRaw(new[] { """{"type":"setMode","mode":"acceptEdits"}""" })[0];

        Assert.Equal(choice.Label, CardPresenter.SuggestionLabel(choice, "Bash"));
    }

    [Fact]
    public void Finished_notice_is_marked_done()
    {
        var card = new NoticeCard
        {
            Id = "notice:s1", CreatedAt = T0, SessionId = "s1", SessionName = "my-app",
            Phase = SessionPhase.Finished, Text = "All done", LastPrompt = "ship it", ClientApp = "Terminal",
        };

        var text = CardPresenter.Present(card, T0.AddHours(2));

        Assert.True(text.IsDone);
        Assert.Equal("All done", text.Body);
        Assert.Equal("my-app · ship it", text.Title);
        Assert.Equal(new[] { "Claude Code", "Terminal", "2h" }, text.Chips);
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
