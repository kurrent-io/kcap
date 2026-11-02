using System.Text.Json;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Util;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A kcap server reduced to what import resumes against: a line that parses and is not noise stores one event, a
/// line number already stored is not stored again, and <c>last-line</c> answers the highest stored line,
/// so lines that produce no event never move it.
/// </summary>
sealed class EventStoringTranscriptServer : IDisposable {
    static readonly HashSet<string> NoiseTypes = ["system", "last-prompt", "file-history-snapshot", "progress", "turn_ended", "session.end", "run.model.configured"];

    readonly WireMockServer                     _server = WireMockServer.Start();
    readonly Dictionary<string, SortedSet<int>> _stored = new(StringComparer.Ordinal);
    readonly List<int>                          _posted = [];
    readonly Lock                               _gate   = new();
    int                                         _sessionEnds;

    public EventStoringTranscriptServer() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithCallback(LastLine));
        _server.Given(Request.Create().WithPath("/hooks/session-start*").UsingPost())
            .RespondWith(Response.Create().WithCallback(SessionStart));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithCallback(Transcript));
        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithCallback(_ => {
                lock (_gate) _sessionEnds++;
                return Json(200, "{}");
            }));
    }

    public string Url => _server.Url!;

    public bool RejectTranscripts { get; set; }

    public int StoredEvents(string sessionId) { lock (_gate) return _stored.TryGetValue(sessionId, out var s) ? s.Count : 0; }

    public int SessionEnds { get { lock (_gate) return _sessionEnds; } }

    /// <summary>Line numbers posted since the last <see cref="ClearPosted"/>.</summary>
    public IReadOnlyList<int> Posted { get { lock (_gate) return [.. _posted]; } }

    public void ClearPosted() {
        lock (_gate) {
            _posted.Clear();
            _sessionEnds = 0;
        }
    }

    /// <summary>Drops every stored event above <paramref name="line"/>, as a restore from an older backup would.</summary>
    public void TruncateAbove(string sessionId, int line) {
        lock (_gate) _stored[sessionId].RemoveWhere(n => n > line);
    }

    ResponseMessage SessionStart(IRequestMessage request) {
        using var doc = JsonDocument.Parse(request.Body!);
        lock (_gate) _stored.TryAdd(doc.RootElement.GetProperty("session_id").GetString()!, []);
        return Json(200, "{}");
    }

    ResponseMessage Transcript(IRequestMessage request) {
        if (RejectTranscripts) return Json(400, "{}");
        using var doc     = JsonDocument.Parse(request.Body!);
        var       root    = doc.RootElement;
        var       session = root.GetProperty("session_id").GetString()!;
        var       lines   = root.GetProperty("lines").EnumerateArray().Select(l => l.GetString()!).ToArray();
        var       numbers = root.GetProperty("line_numbers").EnumerateArray().Select(n => n.GetInt32()).ToArray();

        lock (_gate) {
            var stored = _stored.TryGetValue(session, out var s) ? s : _stored[session] = [];

            for (var i = 0; i < lines.Length; i++) {
                _posted.Add(numbers[i]);
                if (StoresEvent(lines[i])) stored.Add(numbers[i]);
            }
        }

        return Json(200, $$"""{"processed":{{lines.Length}}}""");
    }

    static bool StoresEvent(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("payload_type", out var payloadType) && payloadType.GetString() == "runtime.session"
             && root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("event", out var evt)
             && evt.TryGetProperty("kind", out var kind) && kind.GetString() == "model_completed") return false;
            var type = root.TryGetProperty("role", out var r) ? r
                : root.TryGetProperty("type", out var t) ? t
                : root.GetProperty("payload_type");
            return !NoiseTypes.Contains(type.GetString()!);
        } catch (JsonException) {
            return false;
        }
    }

    ResponseMessage LastLine(IRequestMessage request) {
        var session = request.Path.Split('/')[^2];

        lock (_gate) {
            if (!_stored.TryGetValue(session, out var stored)) return Json(404, "");
            return stored.Count == 0 ? Json(204, "") : Json(200, $$"""{"last_line_number":{{stored.Max}}}""");
        }
    }

    static ResponseMessage Json(int status, string body) => new() {
        StatusCode = status,
        BodyData   = new BodyData { DetectedBodyType = WireMock.Types.BodyType.String, BodyAsString = body },
    };

    public void Dispose() => _server.Stop();
}
