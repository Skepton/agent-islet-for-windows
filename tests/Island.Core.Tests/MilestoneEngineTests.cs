using Island.Core;

namespace Island.Core.Tests;

public class MilestoneEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FiveReset = new(2026, 10, 11, 16, 10, 0, 76, TimeSpan.Zero);
    private static readonly DateTimeOffset SevenReset = new(2026, 10, 14, 13, 0, 0, 76, TimeSpan.Zero);

    private static UsageSnapshot Five(double percent, DateTimeOffset? resets = null) =>
        new(Now, new[] { new UsageWindow(UsageWindowId.FiveHour, percent, resets ?? FiveReset) });

    private static UsageSnapshot Both(double five, double seven) =>
        new(Now, new[]
        {
            new UsageWindow(UsageWindowId.FiveHour, five, FiveReset),
            new UsageWindow(UsageWindowId.SevenDay, seven, SevenReset),
        });

    [Fact]
    public void First_reading_announces_only_the_highest_threshold_already_crossed()
    {
        var engine = new MilestoneEngine();

        var events = engine.Process(Five(28), Now);

        var announced = Assert.Single(events);
        Assert.Equal(25, announced.Threshold);
        Assert.Equal(UsageWindowId.FiveHour, announced.Window);
        Assert.Equal(28, announced.Percent);
    }

    [Fact]
    public void Each_threshold_fires_once_as_usage_climbs()
    {
        var engine = new MilestoneEngine();
        engine.Process(Five(28), Now);

        Assert.Equal(55, Assert.Single(engine.Process(Five(55), Now)).Threshold);
        Assert.Empty(engine.Process(Five(56), Now));
        Assert.Equal(75, Assert.Single(engine.Process(Five(76), Now)).Threshold);
        Assert.Equal(100, Assert.Single(engine.Process(Five(100), Now)).Threshold);
        Assert.Empty(engine.Process(Five(100), Now));
    }

    [Fact]
    public void Default_thresholds_are_every_five_percent()
    {
        var thresholds = new MilestoneOptions().Thresholds;

        Assert.Equal(Enumerable.Range(1, 20).Select(i => i * 5), thresholds);
    }

    [Fact]
    public void Climbing_steadily_announces_every_five_percent_once()
    {
        var engine = new MilestoneEngine();
        var announced = new List<int>();

        for (var percent = 0.0; percent <= 100; percent += 0.5)
            announced.AddRange(engine.Process(Five(percent), Now).Select(e => e.Threshold));

        Assert.Equal(Enumerable.Range(1, 20).Select(i => i * 5), announced);
    }

    [Fact]
    public void A_jump_over_several_thresholds_gives_a_single_event()
    {
        var engine = new MilestoneEngine();
        engine.Process(Five(5), Now);

        var events = engine.Process(Five(82), Now);

        Assert.Equal(80, Assert.Single(events).Threshold);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4.9)]
    public void Below_the_first_threshold_nothing_fires(double percent)
    {
        Assert.Empty(new MilestoneEngine().Process(Five(percent), Now));
    }

    [Fact]
    public void Exactly_on_a_threshold_counts_as_reaching_it()
    {
        Assert.Equal(10, Assert.Single(new MilestoneEngine().Process(Five(10), Now)).Threshold);
        Assert.Equal(5, Assert.Single(new MilestoneEngine().Process(Five(5), Now)).Threshold);
    }

    [Fact]
    public void A_new_reset_time_is_a_new_window_and_starts_fresh()
    {
        var engine = new MilestoneEngine();
        engine.Process(Five(80), Now);

        var next = engine.Process(Five(12, FiveReset.AddHours(5)), Now);

        Assert.Equal(10, Assert.Single(next).Threshold); // fresh window: starts again from the bottom
    }

    [Fact]
    public void A_late_lower_reading_is_ignored_and_does_not_lower_progress()
    {
        var engine = new MilestoneEngine();
        engine.Process(Five(60), Now);

        Assert.Empty(engine.Process(Five(40), Now));
        Assert.Empty(engine.Process(Five(55), Now));
        Assert.Empty(engine.Process(Five(61), Now));
    }

    [Fact]
    public void Both_sources_agree_on_the_window_despite_different_second_precision()
    {
        var engine = new MilestoneEngine();
        engine.Process(Five(28, new DateTimeOffset(2026, 10, 11, 16, 10, 0, 76, TimeSpan.Zero)), Now); // endpoint: fractional seconds

        var fromStatusLine = engine.Process(Five(29, new DateTimeOffset(2026, 10, 11, 16, 10, 0, TimeSpan.Zero)), Now); // status line: whole seconds

        Assert.Empty(fromStatusLine);
    }

    [Fact]
    public void Disabled_windows_never_fire()
    {
        var engine = new MilestoneEngine(new MilestoneOptions { FiveHourEnabled = false });

        var events = engine.Process(Both(80, 80), Now);

        Assert.Equal(UsageWindowId.SevenDay, Assert.Single(events).Window);
    }

    [Fact]
    public void Each_window_is_tracked_separately()
    {
        var events = new MilestoneEngine().Process(Both(55, 12), Now);

        Assert.Equal(55, events.Single(e => e.Window == UsageWindowId.FiveHour).Threshold);
        Assert.Equal(10, events.Single(e => e.Window == UsageWindowId.SevenDay).Threshold);
    }

    [Fact]
    public void Custom_thresholds_are_sorted_deduplicated_and_validated()
    {
        var engine = new MilestoneEngine(new MilestoneOptions { Thresholds = new[] { 50, 10, 10, 200, 0, -5 } });

        Assert.Equal(50, Assert.Single(engine.Process(Five(70), Now)).Threshold);
        Assert.Empty(engine.Process(Five(99), Now));
    }

    [Fact]
    public void Progress_survives_a_restart_through_exported_state()
    {
        var first = new MilestoneEngine();
        first.Process(Five(55), Now);

        var restarted = new MilestoneEngine(state: first.ExportState());

        Assert.Empty(restarted.Process(Five(56), Now));
        Assert.Equal(75, Assert.Single(restarted.Process(Five(76), Now)).Threshold);
    }

    [Fact]
    public void Windows_that_reset_more_than_a_day_ago_are_forgotten()
    {
        var engine = new MilestoneEngine();
        engine.Process(Five(55), Now);

        engine.Process(Five(1, FiveReset.AddDays(3)), Now.AddDays(3));

        Assert.Single(engine.ExportState().Windows);
    }
}

public class MilestoneStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "island-tests-" + Guid.NewGuid().ToString("N"));

    private string StatePath => Path.Combine(_dir, "state.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static MilestoneState SampleState()
    {
        var state = new MilestoneState();
        state.Windows["FiveHour|123"] = new WindowProgress(25, 28.5, new DateTimeOffset(2026, 10, 11, 16, 10, 0, 76, TimeSpan.Zero));
        state.Windows["SevenDay|456"] = new WindowProgress(0, 4, new DateTimeOffset(2026, 10, 14, 13, 0, 0, TimeSpan.Zero));
        return state;
    }

    [Fact]
    public void Saved_state_loads_back_unchanged()
    {
        var store = new MilestoneStateStore(StatePath);

        store.Save(SampleState());
        var loaded = store.Load();

        Assert.Equal(SampleState().Windows, loaded.Windows);
    }

    [Fact]
    public void Saving_twice_replaces_the_file_and_leaves_no_temp_file()
    {
        var store = new MilestoneStateStore(StatePath);
        store.Save(SampleState());

        var updated = SampleState();
        updated.Windows["FiveHour|123"] = new WindowProgress(50, 55, DateTimeOffset.Parse("2026-10-11T16:10:00+00:00"));
        store.Save(updated);

        Assert.Equal(50, store.Load().Windows["FiveHour|123"].NotifiedThreshold);
        Assert.Equal(new[] { "state.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Missing_file_loads_as_empty()
    {
        Assert.Empty(new MilestoneStateStore(StatePath).Load().Windows);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"v":99,"windows":{}}""")]
    [InlineData("""{"v":1,"windows":{"FiveHour|1":{"notified":"x"}}}""")]
    public void Damaged_or_unknown_files_load_as_empty(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, content);

        Assert.Empty(new MilestoneStateStore(StatePath).Load().Windows);
    }

    [Fact]
    public void The_file_holds_only_percentages_and_reset_times()
    {
        var store = new MilestoneStateStore(StatePath);
        store.Save(SampleState());

        var text = File.ReadAllText(StatePath);

        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lastPercent", text);
    }
}
