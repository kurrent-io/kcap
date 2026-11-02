using System.Globalization;

namespace Capacitor.App.ViewModels;

/// When a chat message was recorded, in the viewer's time zone: the time alone for a message from the
/// day it is shown, the date too otherwise, and the full stamp as the tooltip.
public sealed record ChatTimestamp(string Label, string Tip) {
    public static ChatTimestamp? Of(DateTimeOffset? at, TimeProvider time) {
        if (at is not { } stamp) return null;
        var local = TimeZoneInfo.ConvertTime(stamp, time.LocalTimeZone);
        var today = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).Date;
        var format = local.Date == today ? "HH:mm"
            : local.Year == today.Year ? "MMM d, HH:mm"
            : "MMM d yyyy, HH:mm";
        return new ChatTimestamp(
            local.ToString(format, CultureInfo.InvariantCulture),
            local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }
}
