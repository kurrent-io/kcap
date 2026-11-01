using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

public class HookTimingLogTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    static string[] Lines(string path) => File.ReadAllLines(path);

    /// <summary>Each mark carries the time since the previous one and since the OS started the process,
    /// so the first line alone shows what the runtime spent before the entry point ran.</summary>
    [Test]
    public async Task Each_phase_is_one_line_with_its_own_and_the_process_elapsed_time() {
        var time = new FakeTimeProvider(Start);
        var log  = new HookTimingLog(time, Start.AddMilliseconds(-40), 4242, "--claude --no-update-check");
        var path = Tmp.PathTo("logs", HookTimingLog.FileName);
        log.Open(path);

        log.Mark("main");
        time.Advance(TimeSpan.FromMilliseconds(412));
        log.Describe("Stop", "3f2a-77");
        log.Mark("profile");

        var lines = Lines(path);
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).Contains("pid=4242 - - main +0ms @40ms [--claude --no-update-check]");
        await Assert.That(lines[1]).Contains("Stop 3f2a-77 profile +412ms @452ms");
    }

    /// <summary>Phases marked before the log directory is known are kept, not lost.</summary>
    [Test]
    public async Task Marks_before_open_are_written_once_it_opens() {
        var time = new FakeTimeProvider(Start);
        var log  = new HookTimingLog(time, processStart: null, 1, "--claude");
        var path = Tmp.PathTo("logs", HookTimingLog.FileName);

        log.Mark("main");
        time.Advance(TimeSpan.FromMilliseconds(5));
        log.Mark("stdin");
        log.Open(path);

        var lines = Lines(path);
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[1]).EndsWith("stdin +5ms [--claude]");
    }

    /// <summary>The session id and event name come from the payload; anything that could split the line is replaced.</summary>
    [Test]
    public async Task Payload_values_cannot_break_a_line() {
        var log  = new HookTimingLog(new FakeTimeProvider(Start), null, 1, "--claude");
        var path = Tmp.PathTo("logs", HookTimingLog.FileName);
        log.Open(path);

        log.Describe("Stop\nforged 1 line", "a b");
        log.Mark("post");

        var lines = Lines(path);
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("Stop_forged_1_line a_b post");
    }

    [Test]
    [Arguments("1", true)]
    [Arguments("true", true)]
    [Arguments("0", false)]
    [Arguments(null, false)]
    public async Task Only_an_explicit_value_turns_timing_on(string? value, bool expected) =>
        await Assert.That(HookTiming.IsRequested(_ => value)).IsEqualTo(expected);
}
