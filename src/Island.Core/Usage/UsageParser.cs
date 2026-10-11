using System.Globalization;
using System.Text.Json;

namespace Island.Core;

/// <summary>Turns the two known usage payload shapes into a <see cref="UsageSnapshot"/>.</summary>
public static class UsageParser
{
    /// <summary>
    /// Parses the body of GET /api/oauth/usage (utilization 0-100, resets_at ISO-8601).
    /// A window that is null or malformed is skipped. Returns null when the body is not JSON, or has neither
    /// five_hour nor seven_day at all (the endpoint is unofficial; treat that as "shape changed").
    /// </summary>
    public static UsageSnapshot? ParseOAuthUsage(string json, DateTimeOffset now)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var hasFive = root.TryGetProperty("five_hour", out var five);
            var hasSeven = root.TryGetProperty("seven_day", out var seven);
            if (!hasFive && !hasSeven) return null;

            var windows = new List<UsageWindow>();
            if (hasFive && ParseOAuthWindow(UsageWindowId.FiveHour, five) is { } f) windows.Add(f);
            if (hasSeven && ParseOAuthWindow(UsageWindowId.SevenDay, seven) is { } s) windows.Add(s);
            return new UsageSnapshot(now, windows);
        }
    }

    /// <summary>
    /// Parses the status line stdin JSON (rate_limits.*.used_percentage 0-100, resets_at epoch seconds).
    /// Optional source; returns null only when the text is not a JSON object.
    /// </summary>
    public static UsageSnapshot? ParseStatusLine(string json, DateTimeOffset now)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var windows = new List<UsageWindow>();
            if (JsonUtil.Prop(root, "rate_limits") is { } limits)
            {
                AddStatusLineWindow(windows, UsageWindowId.FiveHour, limits, "five_hour");
                AddStatusLineWindow(windows, UsageWindowId.SevenDay, limits, "seven_day");
            }
            return new UsageSnapshot(now, windows);
        }
    }

    private static UsageWindow? ParseOAuthWindow(UsageWindowId id, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (JsonUtil.Prop(element, "utilization") is not { ValueKind: JsonValueKind.Number } utilization
            || !utilization.TryGetDouble(out var percent)) return null;
        if (JsonUtil.Str(element, "resets_at") is not { } text
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var resetsAt)) return null;

        return new UsageWindow(id, Math.Clamp(percent, 0, 100), resetsAt);
    }

    private static void AddStatusLineWindow(List<UsageWindow> windows, UsageWindowId id, JsonElement limits, string name)
    {
        if (JsonUtil.Prop(limits, name) is not { } window) return;
        if (JsonUtil.Prop(window, "used_percentage") is not { ValueKind: JsonValueKind.Number } used
            || !used.TryGetDouble(out var percent)) return;
        if (JsonUtil.Prop(window, "resets_at") is not { ValueKind: JsonValueKind.Number } resets
            || !resets.TryGetDouble(out var epochSeconds)) return;

        windows.Add(new UsageWindow(id, Math.Clamp(percent, 0, 100), DateTimeOffset.FromUnixTimeSeconds((long)epochSeconds)));
    }
}
