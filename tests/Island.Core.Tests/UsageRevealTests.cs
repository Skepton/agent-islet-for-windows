using Island.Core;

namespace Island.Core.Tests;

public sealed class UsageRevealTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Snapshot(double fiveHour, double sevenDay, int resetsInMinutes = 60) =>
        new(T0, new[]
        {
            new UsageWindow(UsageWindowId.FiveHour, fiveHour, T0.AddMinutes(resetsInMinutes)),
            new UsageWindow(UsageWindowId.SevenDay, sevenDay, T0.AddDays(2)),
        });

    [Fact]
    public void Numbers_are_hidden_until_something_reveals_them()
    {
        var reveal = new UsageReveal();

        Assert.False(reveal.IsRevealed(T0));
    }

    [Fact]
    public void The_first_reading_shows_for_a_few_seconds()
    {
        var reveal = new UsageReveal();

        Assert.True(reveal.Observe(Snapshot(20, 40), T0));

        Assert.True(reveal.IsRevealed(T0 + UsageReveal.AfterChange - TimeSpan.FromMilliseconds(1)));
        Assert.False(reveal.IsRevealed(T0 + UsageReveal.AfterChange));
    }

    [Fact]
    public void A_poll_with_the_same_whole_percentages_does_not_reveal_again()
    {
        var reveal = new UsageReveal();
        reveal.Observe(Snapshot(20.2, 40.1), T0);
        var later = T0 + UsageReveal.AfterChange + TimeSpan.FromMinutes(2);

        // Fractions and a different reset time are noise: the endpoint's reset time jitters on every call.
        Assert.False(reveal.Observe(Snapshot(20.4, 39.9, resetsInMinutes: 61), later));
        Assert.False(reveal.IsRevealed(later));
    }

    [Fact]
    public void A_changed_percentage_reveals_again()
    {
        var reveal = new UsageReveal();
        reveal.Observe(Snapshot(20, 40), T0);
        var later = T0 + TimeSpan.FromMinutes(10);

        Assert.True(reveal.Observe(Snapshot(21, 40), later));
        Assert.True(reveal.IsRevealed(later.AddSeconds(5)));
    }

    [Fact]
    public void A_window_that_appears_or_goes_counts_as_a_change()
    {
        var both = Snapshot(20, 40);
        var onlyFiveHour = new UsageSnapshot(T0, new[] { both.Windows[0] });

        Assert.True(UsageReveal.Differs(both, onlyFiveHour));
        Assert.True(UsageReveal.Differs(onlyFiveHour, both));
    }

    [Fact]
    public void Hovering_reveals_and_lingers_after_the_pointer_leaves()
    {
        var reveal = new UsageReveal();

        reveal.Hover(T0);

        Assert.True(reveal.IsRevealed(T0));
        Assert.True(reveal.IsRevealed(T0 + UsageReveal.Linger - TimeSpan.FromMilliseconds(1)));
        Assert.False(reveal.IsRevealed(T0 + UsageReveal.Linger));
    }

    [Fact]
    public void Hovering_never_cuts_a_change_reveal_short()
    {
        var reveal = new UsageReveal();
        reveal.Observe(Snapshot(20, 40), T0);

        reveal.Hover(T0.AddSeconds(1));

        Assert.True(reveal.IsRevealed(T0.AddSeconds(5)));
    }

    [Fact]
    public void Losing_the_reading_hides_the_numbers_at_once()
    {
        var reveal = new UsageReveal();
        reveal.Observe(Snapshot(20, 40), T0);

        Assert.False(reveal.Observe(null, T0.AddSeconds(1)));
        Assert.False(reveal.IsRevealed(T0.AddSeconds(1)));
    }

    [Theory]
    [InlineData(960, 0, true)]     // top-centre
    [InlineData(960, 9, true)]
    [InlineData(960, 10, false)]   // below the trigger strip
    [InlineData(850, 0, true)]     // within 110 px of the centre
    [InlineData(700, 0, false)]    // on the island's width but outside the trigger strip
    [InlineData(1300, 0, false)]
    public void While_hidden_only_a_small_strip_at_the_top_reacts(int x, int y, bool expected)
    {
        var island = new PixelRect(720, 0, 1200, 4); // a thin bar, 4 px tall

        Assert.Equal(expected, UsageReveal.PointerOnBar(island, x, y, scale: 1, revealed: false));
    }

    [Fact]
    public void The_trigger_strip_grows_with_display_scaling()
    {
        var island = new PixelRect(720, 0, 1200, 6);

        Assert.True(UsageReveal.PointerOnBar(island, 960, 14, scale: 1.5, revealed: false)); // 10 dip = 15 px
        Assert.False(UsageReveal.PointerOnBar(island, 960, 15, scale: 1.5, revealed: false));
    }

    [Fact]
    public void Once_revealed_the_whole_window_keeps_it_open()
    {
        var island = new PixelRect(720, 0, 1200, 30);

        Assert.True(UsageReveal.PointerOnBar(island, 730, 25, scale: 1, revealed: true));
        Assert.False(UsageReveal.PointerOnBar(island, 730, 25, scale: 1, revealed: false));
        Assert.False(UsageReveal.PointerOnBar(island, 730, 30, scale: 1, revealed: true));
    }
}
