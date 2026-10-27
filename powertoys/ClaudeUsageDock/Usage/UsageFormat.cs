using System;
using System.Globalization;
using System.Text;

namespace ClaudeUsageDock.Usage;

/// <summary>How a reading is written on the Dock and on the details page.</summary>
internal static class UsageFormat
{
    private const int MeterCells = 5;

    public static string Percent(UsageBar? bar) =>
        bar?.Percent is { } p ? Math.Round(p).ToString(CultureInfo.InvariantCulture) + "%" : "–";

    public static string Meter(UsageBar? bar, int cells = MeterCells)
    {
        var filled = bar?.Percent is { } p ? (int)Math.Round(Math.Clamp(p, 0, 100) / 100 * cells) : 0;
        return new StringBuilder().Append('▰', filled).Append('▱', cells - filled).ToString();
    }

    /// <summary>"2d 5h", "1h 05m", "12m", "<1m"; empty without a reset time.</summary>
    public static string Until(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is not { } w)
        {
            return string.Empty;
        }

        var left = w - now;
        if (left <= TimeSpan.Zero)
        {
            return "now";
        }

        if (left.TotalDays >= 1)
        {
            return $"{(int)left.TotalDays}d {left.Hours}h";
        }

        if (left.TotalHours >= 1)
        {
            return $"{(int)left.TotalHours}h {left.Minutes:00}m";
        }

        return left.TotalMinutes >= 1 ? $"{(int)left.TotalMinutes}m" : "<1m";
    }

    /// <summary>The reset as the widget writes it: "22:10" today, "Sat 11:00" later.</summary>
    public static string ResetClock(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is not { } w)
        {
            return string.Empty;
        }

        var local = w.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>A short word for an error, for the band, where there is no room for a sentence.</summary>
    public static string ErrorWord(string? kind) => kind switch
    {
        "auth" => "key expired",
        "no_credentials" => "needs key",
        "cc_expired" => "run claude",
        "cc_elsewhere" => "CC elsewhere",
        "cc_no_login" or "cc_no_org" => "no login",
        "rate_limited" => "paused",
        "no_plan" => "no plan",
        "bridge" => "bridge error",
        _ => "error",
    };
}
