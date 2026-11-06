namespace Capacitor.App.ViewModels.NextWork;

/// <summary>When a snoozed loose end returns: 09:00 tomorrow in the viewer's zone.</summary>
public static class NextWorkSnoozeTimes {
    static readonly TimeOnly Morning = new(9, 0);

    public static DateTimeOffset Tomorrow(DateTimeOffset now, TimeZoneInfo zone) => MorningOf(LocalDate(now, zone).AddDays(1), zone);

    /// <summary>A time the zone skips or repeats takes its standard offset.</summary>
    public static DateTimeOffset MorningOf(DateOnly day, TimeZoneInfo zone) {
        var local = day.ToDateTime(Morning, DateTimeKind.Unspecified);
        return new(local, zone.GetUtcOffset(local));
    }

    static DateOnly LocalDate(DateTimeOffset now, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
}
