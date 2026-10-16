namespace Island.Core;

/// <summary>A screen rectangle in pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>
/// When the usage numbers are on show while no card needs the user. They stay out of the way: a thin bar (or nothing),
/// and the numbers appear for a few seconds when they change, or while the pointer rests on the bar. Pure logic; the
/// window feeds it snapshots and pointer positions and asks whether to reveal.
/// </summary>
public sealed class UsageReveal
{
    /// <summary>How long new numbers stay on show.</summary>
    public static readonly TimeSpan AfterChange = TimeSpan.FromSeconds(6);

    /// <summary>How long the numbers linger after the pointer leaves, so they do not snap shut.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(1500);

    /// <summary>Width and height of the strip of the bar that reacts to the pointer, in device-independent pixels.</summary>
    public const double TriggerWidth = 220, TriggerHeight = 10;

    private UsageSnapshot? _last;
    private DateTimeOffset _until = DateTimeOffset.MinValue;

    /// <summary>Records a new reading. Returns true when the numbers on show differ from the previous reading.</summary>
    public bool Observe(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        var changed = snapshot is not null && Differs(_last, snapshot);
        _last = snapshot;
        if (snapshot is null) _until = DateTimeOffset.MinValue;
        else if (changed) _until = Max(_until, now + AfterChange);
        return changed;
    }

    /// <summary>The pointer is on the bar (or the revealed strip) right now.</summary>
    public void Hover(DateTimeOffset now) => _until = Max(_until, now + Linger);

    public bool IsRevealed(DateTimeOffset now) => now < _until;

    /// <summary>True when the percentages (rounded to whole numbers) or the set of windows differ. Reset times do not count.</summary>
    public static bool Differs(UsageSnapshot? previous, UsageSnapshot current)
    {
        if (previous is null) return true;
        if (previous.Windows.Count != current.Windows.Count) return true;
        foreach (var window in current.Windows)
        {
            if (previous.Get(window.Id) is not { } before) return true;
            if (Math.Round(before.Percent) != Math.Round(window.Percent)) return true;
        }
        return false;
    }

    /// <summary>
    /// Is the pointer where it should reveal the numbers? While they are hidden only a small strip at the top-centre of
    /// <paramref name="island"/> counts (it is easy to hit but never covers much); once revealed, the whole window counts.
    /// </summary>
    public static bool PointerOnBar(PixelRect island, int x, int y, double scale, bool revealed)
    {
        if (revealed) return island.Contains(x, y);

        var width = Math.Min(island.Width, (int)Math.Round(TriggerWidth * scale));
        var height = Math.Max(island.Height, (int)Math.Round(TriggerHeight * scale));
        var left = island.Left + (island.Width - width) / 2;
        return new PixelRect(left, island.Top, left + width, island.Top + height).Contains(x, y);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
