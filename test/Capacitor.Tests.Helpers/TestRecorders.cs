using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Recording;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Recorders for a test that drives a source call by call, with no server. One source gets one recorder across
/// calls, so a held line's retry and the ack cursor carry over as they do in a real run.
/// </summary>
/// <param name="redactionClock">The clock redaction budgets read, when not <paramref name="time"/>.</param>
public sealed class TestRecorders(ConfigRoot config, TimeProvider? time = null, TimeProvider? redactionClock = null) {
    readonly Dictionary<(string, string?, string, string), TranscriptRecorder> _recorders = [];

    readonly List<string> _log = [];

    public FakeRecorderTransport Transport { get; } = new();

    public FakeRecorderVendor Vendor { get; } = new();

    /// <summary>What the recorders logged, oldest first.</summary>
    public IReadOnlyList<string> Log {
        get {
            lock (_log) return [.._log];
        }
    }

    public TranscriptRecorder For(string sessionId, string transcriptPath, string? agentId, string vendor) {
        lock (_recorders) {
            var key = (sessionId, agentId, vendor, transcriptPath);
            if (_recorders.TryGetValue(key, out var recorder)) return recorder;

            var clock = time ?? TimeProvider.System;

            return _recorders[key] = new TranscriptRecorder(
                new RecordingSource(sessionId, agentId, transcriptPath, vendor, null), Transport, new NoStamps(), clock,
                new RecorderContext(
                    new CapacitorServer("http://127.0.0.1:1", config, Resolutions.None(config)), Vendor, redactionClock ?? clock, Write, () => { }));
        }
    }

    void Write(string message) {
        lock (_log) _log.Add(message);
    }
}
