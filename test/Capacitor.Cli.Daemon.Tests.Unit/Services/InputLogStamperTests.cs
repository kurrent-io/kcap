using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Recording;
using Capacitor.Models.Transcripts.UserMessages;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>How Claude transcript rows are placed against the messages the daemon wrote</summary>
public class InputLogStamperTests {
    const string Session = "0123456789abcdef0123456789abcdef";

    static readonly Guid First  = Guid.Parse("50000000-0000-4000-8000-000000000001");
    static readonly Guid Second = Guid.Parse("50000000-0000-4000-8000-000000000002");
    const string Agent  = "5000000000004000800000000000000a";

    [TempDir] public required TempDir Tmp { get; init; }

    FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero));

    string LogPath => Tmp.PathTo("input-logs", $"{Session}.json");

    InputLog Log() => new(LogPath, Time);

    static InputLogStamper Stamper(InputLog log) => new(log, new PersonRows(TranscriptProjection.For("claude")!, Session, "claude"));

    static string Uuid(int n) => $"60000000-0000-4000-8000-{n:D12}";

    static string Person(int n, string text) => new JsonObject {
        ["type"] = "user", ["uuid"] = Uuid(n), ["parentUuid"] = Uuid(n - 1), ["origin"] = new JsonObject { ["kind"] = "human" },
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
    }.ToJsonString();

    static string Reply(int n) => new JsonObject {
        ["type"] = "assistant", ["uuid"] = Uuid(n), ["parentUuid"] = Uuid(n - 1),
        ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Done." }) },
    }.ToJsonString();

    static string ToolResult(int n) => new JsonObject {
        ["type"] = "user", ["uuid"] = Uuid(n), ["parentUuid"] = Uuid(n - 1),
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "t1", ["content"] = "ok" }) },
    }.ToJsonString();

    static void Write(InputLog log, InputAuthor author, string text) => log.MarkWritten(log.AppendPending(author, text));

    [Test]
    public async Task Identical_texts_from_two_senders_are_told_apart_by_position() {
        var log = Log();
        Write(log, InputAuthor.Dispatch(First, Agent), "run the tests");
        Write(log, InputAuthor.Owner, "run the tests");
        var stamper = Stamper(log);

        await Assert.That(stamper.StampFor(2, Person(2, "run the tests"))).IsEqualTo(new LineStamp(2, First, Agent, IsLocal: false));
        await Assert.That(stamper.StampFor(3, Reply(3))).IsNull();
        await Assert.That(stamper.StampFor(4, ToolResult(4))).IsNull();
        await Assert.That(stamper.StampFor(5, Person(5, "run the tests"))).IsEqualTo(new LineStamp(5, null, null, IsLocal: true));
        await Assert.That(log.IsSuspended).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_row_with_no_matching_entry_goes_unstamped_and_suspends(bool anEntryWaits) {
        var log = Log();
        if (anEntryWaits) Write(log, InputAuthor.Dispatch(First, Agent), "run the tests");
        var stamper = Stamper(log);

        await Assert.That(stamper.StampFor(2, Person(2, "typed at the terminal"))).IsNull();
        await Assert.That(log.IsSuspended).IsTrue();

        Write(log, InputAuthor.Dispatch(Second, Agent), "typed at the terminal");
        await Assert.That(stamper.StampFor(4, Person(4, "run the tests"))).IsNull();
        await Assert.That(stamper.StampFor(5, Person(5, "typed at the terminal"))).IsNull();
    }

    /// <summary>The daemon died after placing a row and before the server took its line, and its successor's recorder
    /// delivers the line again. Two identical messages wait, so only the record can say which one the row was</summary>
    [Test]
    public async Task A_replayed_row_takes_its_stamp_from_the_match_record_and_consumes_no_entry() {
        var log = Log();
        Write(log, InputAuthor.Dispatch(First, Agent), "run the tests");
        Write(log, InputAuthor.Dispatch(Second, Agent), "run the tests");
        await Assert.That(Stamper(log).StampFor(2, Person(2, "run the tests"))).IsEqualTo(new LineStamp(2, First, Agent, IsLocal: false));

        var replay = Stamper(Log());

        await Assert.That(replay.StampFor(2, Person(2, "run the tests"))).IsEqualTo(new LineStamp(2, First, Agent, IsLocal: false));
        await Assert.That(replay.StampFor(2, Person(2, "run the tests"))).IsEqualTo(new LineStamp(2, First, Agent, IsLocal: false));
        await Assert.That(replay.StampFor(4, Person(4, "run the tests"))).IsEqualTo(new LineStamp(4, Second, Agent, IsLocal: false));
    }

    [Test]
    public async Task A_local_message_marks_its_row_local() {
        var log = Log();
        Write(log, InputAuthor.Owner, "look at the failing test");

        await Assert.That(Stamper(log).StampFor(2, Person(2, "look at the failing test"))).IsEqualTo(new LineStamp(2, null, null, IsLocal: true));
    }

    [Test]
    public async Task A_row_read_before_its_write_is_marked_still_matches() {
        var log      = Log();
        var position = log.AppendPending(InputAuthor.Dispatch(First, Agent), "run the tests");

        await Assert.That(Stamper(log).StampFor(2, Person(2, "run the tests"))).IsEqualTo(new LineStamp(2, First, Agent, IsLocal: false));

        log.MarkWritten(position);
        await Assert.That(log.IsSuspended).IsFalse();
        await Assert.That(Log().IsSuspended).IsFalse();
    }

    /// <summary>The batch carries the redacted line, but the entry is a hash of what was pasted, credential and all, so
    /// only the raw line matches it, and the match record holds the raw line's hash</summary>
    [Test]
    public async Task A_redacted_line_still_stamps_by_its_raw_hash() {
        const string text = "push with ghp_0123456789abcdefghijklmnopqrstuvwxyz";
        var raw      = Person(2, text);
        var redacted = SecretRedactor.RedactLine(raw);
        await Assert.That(redacted).IsNotEqualTo(raw);

        var log = Log();
        Write(log, InputAuthor.Dispatch(First, Agent), text);
        var stamper = Stamper(log);

        await Assert.That(stamper.StampFor(2, raw)).IsEqualTo(new LineStamp(2, First, Agent, IsLocal: false));
        await Assert.That(stamper.StampFor(2, raw)).IsEqualTo(new LineStamp(2, First, Agent, IsLocal: false));

        File.Delete(LogPath);
        var fromRedacted = Log();
        Write(fromRedacted, InputAuthor.Dispatch(First, Agent), text);

        await Assert.That(Stamper(fromRedacted).StampFor(2, redacted)).IsNull();
        await Assert.That(fromRedacted.IsSuspended).IsTrue();
    }

    [Test]
    public async Task A_slash_command_matches_the_command_pasted() {
        var log = Log();
        Write(log, InputAuthor.Daemon, "/compact");
        Write(log, InputAuthor.Owner, "carry on");
        var stamper = Stamper(log);
        var command = new JsonObject {
            ["type"] = "user", ["uuid"] = Uuid(2), ["message"] = new JsonObject { ["content"] = "<command-name>/compact</command-name>\n<command-message>compact</command-message>\n<command-args></command-args>" },
        }.ToJsonString();

        await Assert.That(stamper.StampFor(2, command)).IsNull();
        await Assert.That(stamper.StampFor(3, Person(3, "carry on"))).IsEqualTo(new LineStamp(3, null, null, IsLocal: true));
        await Assert.That(log.IsSuspended).IsFalse();
    }

    [Test]
    public async Task Acknowledged_lines_lose_their_match_records() {
        var log = Log();
        Write(log, InputAuthor.Dispatch(First, Agent), "run the tests");
        var stamper = Stamper(log);
        await Assert.That(stamper.StampFor(2, Person(2, "run the tests"))).IsNotNull();

        stamper.Acknowledged(2);

        await Assert.That(stamper.StampFor(2, Person(2, "run the tests"))).IsNull();
        await Assert.That(log.IsSuspended).IsFalse();
    }
}
