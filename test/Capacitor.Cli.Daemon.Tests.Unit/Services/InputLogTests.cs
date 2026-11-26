using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>What the log keeps across a session's life: its place among the messages written, the rows already placed,
/// and whether it may stamp at all, on disk as much as in memory</summary>
public class InputLogTests {
    static readonly Guid First  = Guid.Parse("40000000-0000-4000-8000-000000000001");
    static readonly Guid Second = Guid.Parse("40000000-0000-4000-8000-000000000002");
    const string Agent  = "4000000000004000800000000000000a";

    [TempDir] public required TempDir Tmp { get; init; }

    FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero));

    string LogPath => Tmp.PathTo("input-logs", "session.json");

    InputLog Open() => new(LogPath, Time);

    InputLog Written(params (InputAuthor Author, string Text)[] messages) {
        var log = Open();
        foreach (var (author, text) in messages) log.MarkWritten(log.AppendPending(author, text));

        return log;
    }

    [Test]
    public async Task Idle_and_end_of_file_do_not_resume_stamping() {
        var log = Written((InputAuthor.Dispatch(First, Agent), "run the tests"));
        log.Suspend("a raw Enter reached the terminal");

        Time.Advance(TimeSpan.FromHours(6));
        log.MarkWritten(log.AppendPending(InputAuthor.Dispatch(Second, Agent), "and the linter"));

        await Assert.That(log.Match(4, "line 4", "run the tests")).IsNull();
        await Assert.That(log.Match(5, "line 5", "and the linter")).IsNull();
        await Assert.That(log.IsSuspended).IsTrue();
        await Assert.That(Open().IsSuspended).IsTrue();
    }

    [Test]
    public async Task A_new_process_resumes_stamping_from_the_first_line_after_it() {
        var log = Written((InputAuthor.Dispatch(First, Agent), "run the tests"));
        log.Suspend("a raw Enter reached the terminal");

        log.ResetForNewProcess(firstLine: 10);
        log.MarkWritten(log.AppendPending(InputAuthor.Dispatch(Second, Agent), "run the tests"));

        await Assert.That(log.IsSuspended).IsFalse();
        await Assert.That(log.Match(9, "line 9", "run the tests")).IsNull();
        await Assert.That(log.Match(10, "line 10", "run the tests")).IsEqualTo(new LineStamp(10, Second, Agent, IsLocal: false));
        await Assert.That(log.IsSuspended).IsFalse();
    }

    /// <summary>A session's next process is another agent with its own input stream. A row placed before it came, and
    /// delivered again after, still names the agent its dispatch went to, on disk as much as in memory</summary>
    [Test]
    public async Task Each_stamp_names_the_agent_its_dispatch_went_to() {
        const string next = "4000000000004000800000000000000b";
        var log = Written((InputAuthor.Dispatch(First, Agent), "run the tests"));
        await Assert.That(log.Match(3, "line 3", "run the tests")).IsEqualTo(new LineStamp(3, First, Agent, IsLocal: false));

        log.ResetForNewProcess(firstLine: 4);
        log.MarkWritten(log.AppendPending(InputAuthor.Dispatch(Second, next), "and the linter"));
        var reopened = Open();

        await Assert.That(reopened.Match(3, "line 3", "run the tests")).IsEqualTo(new LineStamp(3, First, Agent, IsLocal: false));
        await Assert.That(reopened.Match(4, "line 4", "and the linter")).IsEqualTo(new LineStamp(4, Second, next, IsLocal: false));
    }

    [Test]
    public async Task Suspension_survives_a_daemon_restart() {
        Written((InputAuthor.Dispatch(First, Agent), "run the tests")).Suspend("a raw Enter reached the terminal");

        var restarted = Open();

        await Assert.That(restarted.IsSuspended).IsTrue();
        await Assert.That(restarted.Match(3, "line 3", "run the tests")).IsNull();
    }

    /// <summary>Nothing shows whether a write recorded as Pending reached the terminal, so its row may or may not come.
    /// One recorded as Written is known to have gone, and its row still matches after the restart</summary>
    [Test]
    public async Task A_Pending_entry_at_recovery_suspends() {
        Open().AppendPending(InputAuthor.Dispatch(First, Agent), "run the tests");

        await Assert.That(Open().IsSuspended).IsTrue();

        File.Delete(LogPath);
        Written((InputAuthor.Dispatch(First, Agent), "run the tests"));
        var recovered = Open();

        await Assert.That(recovered.IsSuspended).IsFalse();
        await Assert.That(recovered.Match(3, "line 3", "run the tests")).IsEqualTo(new LineStamp(3, First, Agent, IsLocal: false));
    }

    [Test]
    public async Task A_match_record_goes_only_after_the_line_is_acknowledged() {
        var log = Written((InputAuthor.Dispatch(First, Agent), "run the tests"), (InputAuthor.Dispatch(Second, Agent), "run the tests"));
        await Assert.That(log.Match(7, "line 7", "run the tests")).IsEqualTo(new LineStamp(7, First, Agent, IsLocal: false));

        log.Acknowledge(6);
        await Assert.That(Open().Match(7, "line 7", "run the tests")).IsEqualTo(new LineStamp(7, First, Agent, IsLocal: false));

        log.Acknowledge(7);
        var reopened = Open();

        // Delivered again after its ack, the line is the server's already, and it takes nothing from the next entry
        await Assert.That(log.Match(7, "line 7", "run the tests")).IsNull();
        await Assert.That(log.IsSuspended).IsFalse();
        await Assert.That(reopened.Match(8, "line 8", "run the tests")).IsEqualTo(new LineStamp(8, Second, Agent, IsLocal: false));
    }

    /// <summary>A line placed before that comes back changed is no longer the row that was placed. The line as it was
    /// still is, so its record goes on answering, but nothing new is placed</summary>
    [Test]
    public async Task A_mismatched_match_record_suspends() {
        var log = Written((InputAuthor.Dispatch(First, Agent), "run the tests"), (InputAuthor.Dispatch(Second, Agent), "and the linter"));
        await Assert.That(log.Match(7, "line 7", "run the tests")).IsNotNull();

        await Assert.That(log.Match(7, "line 7, rewritten", "run the tests")).IsNull();
        await Assert.That(log.IsSuspended).IsTrue();
        await Assert.That(log.Match(7, "line 7", "run the tests")).IsEqualTo(new LineStamp(7, First, Agent, IsLocal: false));
        await Assert.That(log.Match(8, "line 8", "and the linter")).IsNull();
    }

    [Test]
    public async Task The_log_holds_no_message_text() {
        const string secret = "deploy with the staging key and tell nobody";
        var log = Written((InputAuthor.Dispatch(First, Agent), secret), (InputAuthor.Owner, secret + " twice"));
        await Assert.That(log.Match(2, $$$"""{"type":"user","message":{"content":"{{{secret}}}"}}""", secret)).IsNotNull();

        var stored = await File.ReadAllTextAsync(LogPath);

        await Assert.That(stored).DoesNotContain("staging");
        await Assert.That(stored).DoesNotContain("nobody");
    }

    [Test]
    public async Task A_log_that_cannot_be_read_loads_suspended() {
        Tmp.CreateFile(Path.Combine("input-logs", "session.json"), "{ not json");

        await Assert.That(Open().IsSuspended).IsTrue();
    }

    [Test]
    public async Task The_daemons_own_text_consumes_its_entry_and_stamps_nothing() {
        var log = Written((InputAuthor.Daemon, "/compact"), (InputAuthor.Owner, "carry on"));

        await Assert.That(log.Match(3, "line 3", "/compact")).IsNull();
        await Assert.That(log.Match(4, "line 4", "carry on")).IsEqualTo(new LineStamp(4, null, null, IsLocal: true));
        await Assert.That(log.IsSuspended).IsFalse();
    }

    [Test]
    public async Task An_abandoned_entry_waits_for_no_row() {
        var log       = Open();
        var abandoned = log.AppendPending(InputAuthor.Dispatch(First, Agent), "never pasted");
        log.Abandon(abandoned);
        log.MarkWritten(log.AppendPending(InputAuthor.Dispatch(Second, Agent), "pasted"));

        await Assert.That(log.Match(3, "line 3", "pasted")).IsEqualTo(new LineStamp(3, Second, Agent, IsLocal: false));
        await Assert.That(Open().IsSuspended).IsFalse();
    }

    [Test]
    public async Task A_line_that_is_not_a_persons_row_changes_nothing() {
        var log = Written((InputAuthor.Dispatch(First, Agent), "run the tests"));

        await Assert.That(log.Match(3, "an assistant line", null)).IsNull();
        await Assert.That(log.IsSuspended).IsFalse();
        await Assert.That(log.Match(4, "line 4", "run the tests")).IsEqualTo(new LineStamp(4, First, Agent, IsLocal: false));
    }

    static string[] Informed(CapturingLogger<InputLog> logger) {
        lock (logger.Entries) return [.. logger.Entries.Where(entry => entry.Level == LogLevel.Information).Select(entry => entry.Message)];
    }

    /// <summary>The week of comparison counts suspended sessions from the daemon's log, so a suspension says why once,
    /// however many rows go unstamped behind it, and a new process says it starts stamping again</summary>
    [Test]
    public async Task A_suspension_logs_its_reason_once() {
        var logger = new CapturingLogger<InputLog>();
        var log    = new InputLog(LogPath, Time, logger, "session-1");
        log.MarkWritten(log.AppendPending(InputAuthor.Dispatch(First, Agent), "run the tests"));

        await Assert.That(log.Match(3, "line 3", "typed at the terminal")).IsNull();
        await Assert.That(log.Match(4, "line 4", "run the tests")).IsNull();
        await Assert.That(log.Match(5, "line 5", "and again")).IsNull();
        log.Suspend("a raw Enter reached the terminal");

        var suspended = Informed(logger);
        await Assert.That(suspended).Count().IsEqualTo(1);
        await Assert.That(suspended[0]).Contains("session-1");
        await Assert.That(suspended[0]).Contains("a person-authored row is not the next message");

        log.ResetForNewProcess(firstLine: 6);

        await Assert.That(Informed(logger)).Count().IsEqualTo(2);
        await Assert.That(Informed(logger)[1]).Contains("session-1");
        await Assert.That(Informed(logger)[1]).Contains("6");
    }

    [Test]
    public async Task A_log_that_cannot_be_written_warns_and_suspends() {
        var logger  = new CapturingLogger<InputLog>();
        var blocker = Tmp.CreateFile("blocker");
        var log     = new InputLog(Path.Combine(blocker, "session.json"), Time, logger, "session-1");

        log.MarkWritten(log.AppendPending(InputAuthor.Dispatch(First, Agent), "run the tests"));

        await Assert.That(log.IsSuspended).IsTrue();
        await Assert.That(logger.Entries.Count(entry => entry.Level == LogLevel.Warning)).IsEqualTo(1);
        await Assert.That(logger.Entries.Single(entry => entry.Level == LogLevel.Warning).Message).Contains("session-1");
    }

    /// <summary>A trailing newline is the submit's, not the message's, whichever side carries it</summary>
    [Test]
    public async Task Trailing_newlines_do_not_tell_texts_apart() {
        var log = Written((InputAuthor.Dispatch(First, Agent), "run the tests\n"), (InputAuthor.Dispatch(Second, Agent), "and the linter"));

        await Assert.That(log.Match(3, "line 3", "run the tests")).IsEqualTo(new LineStamp(3, First, Agent, IsLocal: false));
        await Assert.That(log.Match(4, "line 4", "and the linter\r\n")).IsEqualTo(new LineStamp(4, Second, Agent, IsLocal: false));
    }
}
