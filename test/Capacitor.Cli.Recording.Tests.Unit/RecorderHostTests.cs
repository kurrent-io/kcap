using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Recording.Tests.Unit;

public class RecorderHostTests {
    const string Secret = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";
    const string Prompt = """{"type":"user","message":{"content":"Summarise the README"}}""";
    const string Reply  = """{"type":"assistant","message":{"content":[{"type":"text","text":"Done."}]}}""";
    const string Later  = """{"type":"assistant","message":{"content":[{"type":"text","text":"And more."}]}}""";
    const string Held   = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"key " + Secret + "\"}]}}";

    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeRecorderTransport _transport = new() { IsConnected = true };
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));

    readonly ConcurrentDictionary<string, int> _ticks = new();
    readonly ConcurrentDictionary<string, Func<Task>?> _beforeDrain = new();
    readonly ConcurrentDictionary<string, TimeProvider> _clocks = new();
    readonly ConcurrentQueue<string> _log = new();

    ConfigRoot Config => field ??= new(Tmp.CreateDir("config"));

    RecorderHost NewHost(Func<string, FileStream>? open = null) => new(
        _transport, _ => new NoStamps(),
        source => new RecorderContext(
            new CapacitorServer("http://127.0.0.1:1", Config, Resolutions.None(Config)),
            new FakeRecorderHousekeeping {
                BeforeDrain = () => {
                    _ticks.AddOrUpdate(source.Key, 1, (_, n) => n + 1);

                    return _beforeDrain.TryGetValue(source.Key, out var hook) && hook is not null ? hook() : Task.CompletedTask;
                },
            },
            _clocks.GetValueOrDefault(source.Key, _time), message => _log.Enqueue($"{source.Key}: {message}"), () => { }),
        _time, open);

    static RecordingSource Source(string path, string? agentId = null, string? sessionId = null) =>
        new(sessionId ?? Guid.NewGuid().ToString("N"), agentId, path, "claude", null);

    int Ticks(string key) => _ticks.GetValueOrDefault(key);

    int LinesSent(string sessionId) => _transport.Batches.Where(batch => batch.SessionId == sessionId).Sum(batch => batch.Lines.Length);

    /// <summary>Waits for <paramref name="condition"/>, moving the clock by <paramref name="step"/> on each look.</summary>
    async Task Eventually(Func<bool> condition, string what, TimeSpan? step = null) {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);

        while (!condition()) {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Never saw: {what}");

            if (step is { } advance) _time.Advance(advance);

            await Task.Delay(5);
        }
    }

    [Test]
    public async Task A_redaction_hold_stalls_only_its_own_source() {
        var stalled = new SwitchableRedactionClock { Exhausted = false };
        var heldSource = Source(Tmp.CreateFile("held.jsonl", $"{Prompt}\n"), "a1");
        var freeSource = Source(Tmp.CreateFile("free.jsonl", $"{Prompt}\n"), "a2");
        _clocks[heldSource.Key] = stalled;
        var host = NewHost();

        host.TryAdd(heldSource);
        host.TryAdd(freeSource);
        await Eventually(() => LinesSent(heldSource.SessionId) == 1 && LinesSent(freeSource.SessionId) == 1, "the first line of each");

        stalled.Exhausted = true;
        await File.AppendAllTextAsync(heldSource.TranscriptPath, $"{Held}\n{Later}\n");
        await File.AppendAllTextAsync(freeSource.TranscriptPath, $"{Reply}\n{Later}\n");
        await Eventually(() => LinesSent(freeSource.SessionId) == 3, "the free source's lines", TimeSpan.FromSeconds(1));
        var ticksWhenFreeCaughtUp = Ticks(heldSource.Key);
        await Eventually(() => Ticks(heldSource.Key) >= ticksWhenFreeCaughtUp + 3, "ticks while held", TimeSpan.FromSeconds(1));

        await Assert.That(LinesSent(heldSource.SessionId)).IsEqualTo(1);

        stalled.Exhausted = false;
        await Eventually(() => LinesSent(heldSource.SessionId) == 3, "the held source to catch up", TimeSpan.FromMinutes(5));

        var heldText = string.Concat(_transport.Batches.Where(batch => batch.SessionId == heldSource.SessionId).SelectMany(batch => batch.Lines));
        await Assert.That(heldText).DoesNotContain(Secret);
    }

    [Test]
    public async Task A_throwing_source_does_not_stop_the_others() {
        var failing = Source(Tmp.CreateFile("failing.jsonl", $"{Prompt}\n"), "a1");
        var healthy = Source(Tmp.CreateFile("healthy.jsonl", $"{Prompt}\n"), "a2");
        _beforeDrain[failing.Key] = () => throw new InvalidOperationException("boom");
        var host = NewHost();

        host.TryAdd(failing);
        host.TryAdd(healthy);
        await Eventually(() => !host.ActiveKeys.Contains(failing.Key), "the failing source to leave");
        await File.AppendAllTextAsync(healthy.TranscriptPath, $"{Reply}\n");
        await Eventually(() => LinesSent(healthy.SessionId) == 2, "the healthy source's lines", TimeSpan.FromSeconds(1));

        await Assert.That(host.ActiveKeys).IsEquivalentTo([healthy.Key]);
        await Assert.That(_log.Any(line => line.StartsWith($"{failing.Key}: Recording", StringComparison.Ordinal) && line.Contains("boom"))).IsTrue();

        _beforeDrain.TryRemove(failing.Key, out _);
        await Assert.That(host.TryAdd(failing)).IsTrue();
        await Eventually(() => LinesSent(failing.SessionId) == 1, "the failed source to record again");
    }

    [Test]
    public async Task Finishing_a_source_leaves_the_others_running() {
        var sessionId = Guid.NewGuid().ToString("N");
        var parent    = Source(Tmp.CreateFile("parent.jsonl", $"{Prompt}\n{Reply}\n"), sessionId: sessionId);
        var child     = Source(Tmp.CreateFile("child.jsonl", $"{Reply}\n"), "a1", sessionId);
        var host      = NewHost();

        host.TryAdd(parent);
        host.TryAdd(child);
        await Eventually(() => LinesSent(sessionId) == 3, "both sources' lines");

        await host.FinishAsync(child.Key, CancellationToken.None).WithinDeadlineAsync("finishing the child");

        await Assert.That(host.ActiveKeys).IsEquivalentTo([parent.Key]);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);

        await File.AppendAllTextAsync(parent.TranscriptPath, $"{Later}\n");
        await Eventually(() => LinesSent(sessionId) == 4, "the parent still recording", TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Finishing_a_key_nothing_records_is_a_no_op() {
        await NewHost().FinishAsync("nothing", CancellationToken.None).WithinDeadlineAsync("finishing an unknown key");
    }

    [Test]
    public async Task Adding_a_key_twice_is_refused() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Prompt}\n"));
        var host   = NewHost();

        await Assert.That(host.TryAdd(source)).IsTrue();
        await Assert.That(host.TryAdd(source)).IsFalse();

        await Assert.That(host.ActiveKeys).IsEquivalentTo([source.Key]);
    }

    [Test]
    public async Task A_stop_for_one_source_ends_only_that_source() {
        var stopped = Source(Tmp.CreateFile("stopped.jsonl", $"{Prompt}\n"), "a1");
        var kept    = Source(Tmp.CreateFile("kept.jsonl", $"{Prompt}\n"), "a2");
        var host    = NewHost();

        host.TryAdd(stopped);
        host.TryAdd(kept);
        await Eventually(() => _transport.Connects == 2, "both sources to register");

        await _transport.RaiseStopRequestedAsync(stopped.Key, "the server asked");
        await Eventually(() => !host.ActiveKeys.Contains(stopped.Key), "the stopped source to leave");

        await Assert.That(host.ActiveKeys).IsEquivalentTo([kept.Key]);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
    }

    [Test]
    public async Task A_source_that_goes_idle_leaves_and_stops_listening() {
        var source = Source(Tmp.CreateFile("t.jsonl", $"{Reply}\n"), "a1");
        var host   = NewHost();

        host.TryAdd(source);
        await Eventually(() => LinesSent(source.SessionId) == 1, "its line");

        await Eventually(() => !host.ActiveKeys.Contains(source.Key), "the idle end", TimeSpan.FromHours(7));
        await _transport.RaiseReconnectedAsync();

        await Assert.That(_transport.Connects).IsEqualTo(1);
        await Assert.That(_transport.DrainCompletes).IsEqualTo(1);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_handler_running_after_its_source_ended_does_not_starve_the_next(bool reconnect) {
        var first  = Source(Tmp.CreateFile("first.jsonl", $"{Prompt}\n"), "a1");
        var second = Source(Tmp.CreateFile("second.jsonl", $"{Prompt}\n"), "a2");
        var host   = NewHost();

        host.TryAdd(first);
        await Eventually(() => _transport.Connects == 1, "the first source to register");

        // Raised to a snapshot of the handlers, as the hub does, so the second's handler runs after it has ended.
        Task Probe() => host.FinishAsync(second.Key, CancellationToken.None);
        _transport.StopRequested += (key, _) => key == second.Key ? Probe() : Task.CompletedTask;
        _transport.Reconnected   += Probe;

        host.TryAdd(second);
        await Eventually(() => _transport.Connects == 2, "the second source to register");

        if (reconnect) await _transport.RaiseReconnectedAsync();
        else await _transport.RaiseStopRequestedAsync(second.Key, "the server asked");

        await Assert.That(host.ActiveKeys).IsEquivalentTo([first.Key]);
    }

    [Test]
    public async Task Fifty_idle_sources_read_a_bounded_amount_on_a_tick() {
        const int Sources = 50;
        const int IdleTicks = 10;
        const int PerTickBudget = 8 * 1024;

        var read = new ConcurrentDictionary<string, long[]>();

        var body    = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + new string('x', 1500) + "\"}]}}\n";
        var sources = Enumerable.Range(0, Sources)
            .Select(i => Source(Tmp.CreateFile($"s{i}.jsonl", string.Concat(Enumerable.Repeat(body, 60))), $"a{i}"))
            .ToList();
        var host = NewHost(path => new CountingFileStream(path, read.GetOrAdd(path, _ => new long[1])));

        foreach (var source in sources) host.TryAdd(source);

        await Eventually(() => sources.All(source => LinesSent(source.SessionId) == 60), "every source's first read");
        await Eventually(() => sources.All(source => Ticks(source.Key) >= 2), "a tick past the first read", TimeSpan.FromSeconds(1));

        var ticksBefore = sources.ToDictionary(source => source.Key, source => Ticks(source.Key));
        var bytesBefore = sources.ToDictionary(source => source.Key, source => Interlocked.Read(ref read[source.TranscriptPath][0]));

        await Eventually(() => sources.All(source => Ticks(source.Key) >= ticksBefore[source.Key] + IdleTicks), "idle ticks", TimeSpan.FromSeconds(1));

        // A reading in flight when a count was taken can land on either side of it, so the window is one tick wider.
        var worst = sources.Max(source => {
            var bytes = Interlocked.Read(ref read[source.TranscriptPath][0]) - bytesBefore[source.Key];
            var ticks = Ticks(source.Key) - ticksBefore[source.Key] + 1;

            return bytes / ticks;
        });

        await Assert.That(new FileInfo(sources[0].TranscriptPath).Length).IsGreaterThan(90_000);
        await Assert.That(worst).IsLessThan(PerTickBudget);
        await Assert.That(host.ActiveKeys.Count).IsEqualTo(Sources);

        Console.WriteLine($"Worst idle read per tick: {worst} bytes");
    }

    // Only the outermost call counts: a derived FileStream's async reads call back into its own sync overloads.
    sealed class CountingFileStream(string path, long[] bytesRead) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) {
        int _depth;

        public override int Read(byte[] buffer, int offset, int count) => Count(() => base.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) {
            _depth++;
            try {
                var n = base.Read(buffer);
                if (_depth == 1) Interlocked.Add(ref bytesRead[0], n);

                return n;
            } finally {
                _depth--;
            }
        }

        public override int ReadByte() {
            _depth++;
            try {
                var b = base.ReadByte();
                if (_depth == 1 && b >= 0) Interlocked.Increment(ref bytesRead[0]);

                return b;
            } finally {
                _depth--;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
            _depth++;
            try {
                var n = await base.ReadAsync(buffer, ct);
                if (_depth == 1) Interlocked.Add(ref bytesRead[0], n);

                return n;
            } finally {
                _depth--;
            }
        }

        int Count(Func<int> read) {
            _depth++;
            try {
                var n = read();
                if (_depth == 1) Interlocked.Add(ref bytesRead[0], n);

                return n;
            } finally {
                _depth--;
            }
        }
    }
}
