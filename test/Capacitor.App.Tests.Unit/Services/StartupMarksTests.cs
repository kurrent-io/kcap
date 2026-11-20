using System.Text.Json.Nodes;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit.Services;

public class StartupMarksTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static JsonObject[] Lines(ConfigRoot config) =>
        [.. File.ReadAllLines(config.Path(LifecycleLog.FileName)).Select(line => JsonNode.Parse(line)!.AsObject())];

    [Test]
    public async Task A_mark_is_stamped_with_the_milliseconds_since_the_process_started() {
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var clock = new FakeTimeProvider();
        var started = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromMilliseconds(1234.9));
        var marks = new StartupMarks(new LifecycleLog(config, clock, "/home/u"), clock, started);

        marks.Mark("window_shown", ("executable", "/app"));

        var line = Lines(config).Single();
        await Assert.That(line["event"]!.GetValue<string>()).IsEqualTo("window_shown");
        await Assert.That(line[StartupMarks.SinceStartField]!.GetValue<string>()).IsEqualTo("1234");
        await Assert.That(line["executable"]!.GetValue<string>()).IsEqualTo("/app");
    }

    /// The stamp is the moment passed in, not the moment the write ran.
    [Test]
    public async Task A_mark_observed_earlier_keeps_its_own_time() {
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var clock = new FakeTimeProvider();
        var started = clock.GetUtcNow();
        var marks = new StartupMarks(new LifecycleLog(config, clock, "/home/u"), clock, started);
        var at = started.AddMilliseconds(500);
        clock.Advance(TimeSpan.FromSeconds(3));

        marks.Mark("first_sessions_painted", at);

        await Assert.That(Lines(config).Single()[StartupMarks.SinceStartField]!.GetValue<string>()).IsEqualTo("500");
    }

    [Test]
    public async Task Each_mark_is_written_once_per_process() {
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var clock = new FakeTimeProvider();
        var marks = new StartupMarks(new LifecycleLog(config, clock, "/home/u"), clock, clock.GetUtcNow());

        var first = marks.Mark("window_shown");
        var second = marks.Mark("window_shown");
        marks.Mark("tray_created");

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
        await Assert.That(Lines(config).Select(line => line["event"]!.GetValue<string>()))
            .IsEquivalentTo(["window_shown", "tray_created"]);
    }

    [Test]
    public async Task An_unknown_process_start_omits_the_stamp() {
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var clock = new FakeTimeProvider();
        var marks = new StartupMarks(new LifecycleLog(config, clock, "/home/u"), clock, processStart: null);

        marks.Mark("app_started");

        await Assert.That(Lines(config).Single().ContainsKey(StartupMarks.SinceStartField)).IsFalse();
    }

    [Test]
    public async Task The_current_process_started_in_the_past() {
        var start = StartupMarks.CurrentProcessStart();

        await Assert.That(start).IsNotNull();
        await Assert.That(start!.Value).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
        await Assert.That(DateTimeOffset.UtcNow - start.Value).IsLessThan(TimeSpan.FromDays(1));
    }
}
