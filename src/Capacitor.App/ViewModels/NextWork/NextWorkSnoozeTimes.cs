namespace Capacitor.App.ViewModels.NextWork;

/// <summary>The instants the snooze menu offers. Calendar choices land at 09:00 in the viewer's zone.</summary>
public static class NextWorkSnoozeTimes {
    static readonly TimeOnly Morning = new(9, 0);

    public static DateTimeOffset InThreeDays(DateTimeOffset now) => now.AddHours(72);

    public static DateTimeOffset Tomorrow(DateTimeOffset now, TimeZoneInfo zone) => MorningOf(LocalDate(now, zone).AddDays(1), zone);

    public static DateTimeOffset NextWeek(DateTimeOffset now, TimeZoneInfo zone) => MorningOf(LocalDate(now, zone).AddDays(7), zone);

    /// <summary>A time the zone skips or repeats takes its standard offset.</summary>
    public static DateTimeOffset MorningOf(DateOnly day, TimeZoneInfo zone) {
        var local = day.ToDateTime(Morning, DateTimeKind.Unspecified);
        return new(local, zone.GetUtcOffset(local));
    }

    static DateOnly LocalDate(DateTimeOffset now, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
}
