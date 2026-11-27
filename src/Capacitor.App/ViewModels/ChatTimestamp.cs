using System.Globalization;

namespace Capacitor.App.ViewModels;

/// When a chat message was recorded, in the viewer's time zone: the time alone for a message from the
/// day it is shown, with Yesterday or the weekday within the past week, the date before that, and the
/// full stamp as the tooltip.
public sealed record ChatTimestamp(string Label, string Tip) {
    public static ChatTimestamp? Of(DateTimeOffset? at, TimeProvider time) {
        if (at is not { } stamp) return null;
        var local = TimeZoneInfo.ConvertTime(stamp, time.LocalTimeZone);
        var today = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).Date;
        var daysAgo = (today - local.Date).Days;
        var label = daysAgo switch {
            0 => local.ToString("HH:mm", CultureInfo.InvariantCulture),
            1 => local.ToString("'Yesterday' HH:mm", CultureInfo.InvariantCulture),
            > 1 and < 7 => local.ToString("dddd HH:mm", CultureInfo.InvariantCulture),
            _ when local.Year == today.Year => local.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture),
            _ => local.ToString("MMM d yyyy, HH:mm", CultureInfo.InvariantCulture),
        };
        return new ChatTimestamp(
            label,
            local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }
}
