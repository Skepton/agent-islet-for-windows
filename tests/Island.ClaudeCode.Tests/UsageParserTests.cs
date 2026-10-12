using Island.ClaudeCode;
using Island.Core;

namespace Island.ClaudeCode.Tests;

public class UsageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static string Fixture(string name) => File.ReadAllText(Payloads.Fixture("usage", name));

    [Fact]
    public void Real_oauth_response_gives_both_windows()
    {
        var snapshot = UsageParser.ParseOAuthUsage(Fixture("oauth-usage.response.json"), Now);

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Windows.Count);

        var five = snapshot.Get(UsageWindowId.FiveHour)!;
        Assert.Equal(28.0, five.Percent);
        Assert.Equal(DateTimeOffset.Parse("2026-10-11T16:10:00.076076+00:00"), five.ResetsAt);

        var seven = snapshot.Get(UsageWindowId.SevenDay)!;
        Assert.Equal(4.0, seven.Percent);
        Assert.Equal(DateTimeOffset.Parse("2026-10-14T13:00:00.076101+00:00"), seven.ResetsAt);
    }

    [Fact]
    public void Null_window_is_skipped()
    {
        var json = """{"five_hour":null,"seven_day":{"utilization":10,"resets_at":"2026-10-14T13:00:00+00:00"}}""";

        var snapshot = UsageParser.ParseOAuthUsage(json, Now);

        Assert.Equal(UsageWindowId.SevenDay, Assert.Single(snapshot!.Windows).Id);
    }

    [Fact]
    public void Both_windows_null_is_an_empty_snapshot_not_a_schema_change()
    {
        var snapshot = UsageParser.ParseOAuthUsage("""{"five_hour":null,"seven_day":null}""", Now);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Windows);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"error":"nope"}""")]
    public void Unrecognised_bodies_are_reported_as_unknown_shape(string body)
    {
        Assert.Null(UsageParser.ParseOAuthUsage(body, Now));
    }

    [Fact]
    public void Malformed_windows_are_skipped_individually()
    {
        var json = """{"five_hour":{"utilization":"lots","resets_at":"2026-10-11T16:10:00+00:00"},"seven_day":{"utilization":5,"resets_at":"garbage"}}""";

        var snapshot = UsageParser.ParseOAuthUsage(json, Now);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public void Percent_is_clamped_to_0_100()
    {
        var json = """{"five_hour":{"utilization":140,"resets_at":"2026-10-11T16:10:00+00:00"},"seven_day":{"utilization":-3,"resets_at":"2026-10-14T13:00:00+00:00"}}""";

        var snapshot = UsageParser.ParseOAuthUsage(json, Now)!;

        Assert.Equal(100, snapshot.Get(UsageWindowId.FiveHour)!.Percent);
        Assert.Equal(0, snapshot.Get(UsageWindowId.SevenDay)!.Percent);
    }

    [Fact]
    public void Status_line_payload_uses_used_percentage_and_epoch_seconds()
    {
        var snapshot = UsageParser.ParseStatusLine(Fixture("statusline.rate_limits.json"), Now);

        Assert.NotNull(snapshot);
        var five = snapshot.Get(UsageWindowId.FiveHour)!;
        Assert.Equal(23.5, five.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1738425600), five.ResetsAt);
        Assert.Equal(41.2, snapshot.Get(UsageWindowId.SevenDay)!.Percent);
    }

    [Fact]
    public void Status_line_without_rate_limits_is_an_empty_snapshot()
    {
        var snapshot = UsageParser.ParseStatusLine("""{"session_id":"x"}""", Now);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Windows);
    }
}
