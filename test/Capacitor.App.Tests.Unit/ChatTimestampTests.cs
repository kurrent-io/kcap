using System.Globalization;
using Capacitor.App.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class ChatTimestampTests {
    static FakeTimeProvider At(string utcNow, TimeZoneInfo zone) {
        var time = new FakeTimeProvider(Utc(utcNow));
        time.SetLocalTimeZone(zone);
        return time;
    }

    static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone("plus-two", TimeSpan.FromHours(2), "plus-two", "plus-two");

    [Test]
    public async Task No_stamp_shows_no_time() =>
        await Assert.That(ChatTimestamp.Of(null, At("2026-11-02T12:00:00Z", TimeZoneInfo.Utc))).IsNull();

    [Test]
    public async Task A_message_from_today_shows_the_local_time_only() {
        var stamp = ChatTimestamp.Of(Utc("2026-11-02T07:05:09Z"), At("2026-11-02T12:00:00Z", PlusTwo))!;

        await Assert.That(stamp.Label).IsEqualTo("09:05");
        await Assert.That(stamp.Tip).IsEqualTo("2026-11-02 09:05:09");
    }

    /// 23:30 UTC is already the next day two hours east, so "today" must be decided in the viewer's zone.
    [Test]
    public async Task Today_is_decided_in_the_viewers_time_zone() {
        var stamp = ChatTimestamp.Of(Utc("2026-11-01T23:30:00Z"), At("2026-11-02T08:00:00Z", PlusTwo))!;

        await Assert.That(stamp.Label).IsEqualTo("01:30");
    }

    [Test]
    public async Task An_earlier_day_shows_the_date() {
        var stamp = ChatTimestamp.Of(Utc("2026-10-30T16:45:00Z"), At("2026-11-02T12:00:00Z", TimeZoneInfo.Utc))!;

        await Assert.That(stamp.Label).IsEqualTo("Oct 30, 16:45");
    }

    [Test]
    public async Task An_earlier_year_shows_the_year() {
        var stamp = ChatTimestamp.Of(Utc("2025-12-31T16:45:00Z"), At("2026-11-02T12:00:00Z", TimeZoneInfo.Utc))!;

        await Assert.That(stamp.Label).IsEqualTo("Dec 31 2025, 16:45");
    }
}
