namespace Island.Core;

/// <summary>Text and thresholds shared by the usage strip and milestone cards.</summary>
public static class UsageFormat
{
    public static string WindowLabel(UsageWindowId id) => id == UsageWindowId.FiveHour ? "5h" : "7d";

    /// <summary>"2d", "1h 10m", "45m", "now".</summary>
    public static string Countdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "now";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}d";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }

    /// <summary>"&lt;1m", "5m", "3h", "2d": how long ago something started.</summary>
    public static string Age(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromMinutes(1)) return "<1m";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}m";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours}h";
        return $"{(int)elapsed.TotalDays}d";
    }

    /// <summary>0 = fine (green), 1 = getting high (amber), 2 = nearly out (red).</summary>
    public static int Level(double percent) => percent >= 90 ? 2 : percent >= 70 ? 1 : 0;
}
