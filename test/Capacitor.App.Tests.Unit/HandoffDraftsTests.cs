using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class HandoffDraftsTests {
    static readonly HandoffDraft Draft = new("carry on", [], "codex", "", null, null, null, null);

    /// <summary>A resume that worked never reports back, so its hold, and the prompt text in it, must expire on its own
    /// rather than stay for the app's lifetime.</summary>
    [Test]
    public async Task A_resume_hold_nothing_settles_expires() {
        var time = new FakeTimeProvider();
        using var failures = new Subject<LaunchFailure>();
        using var drafts = new HandoffDrafts(failures, directory: null, time);

        drafts.HoldResume("source-1", "agent-1", Draft);
        time.Advance(TimeSpan.FromMinutes(9));
        await Assert.That(drafts.PendingCountForTesting).IsEqualTo(1);

        time.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(drafts.PendingCountForTesting).IsEqualTo(0);
    }

    /// <summary>A resume whose agent never showed up by expiry did not start as far as the app knows: its draft goes
    /// back to the source instead of vanishing with the hold.</summary>
    [Test]
    public async Task An_expired_resume_that_never_started_returns_its_draft() {
        var time = new FakeTimeProvider();
        using var failures = new Subject<LaunchFailure>();
        using var drafts = new HandoffDrafts(failures, directory: null, time);

        drafts.HoldResume("source-1", "agent-1", Draft);
        time.Advance(TimeSpan.FromMinutes(10));

        await Assert.That(drafts.TakeFailed("source-1")?.Text).IsEqualTo("carry on");
        await Assert.That(drafts.ResumeFailed("source-1")).IsTrue();
    }

    /// <summary>A resume whose agent showed up as a row started: its expiry returns nothing.</summary>
    [Test]
    public async Task An_expired_resume_that_started_returns_nothing() {
        var time = new FakeTimeProvider();
        using var failures = new Subject<LaunchFailure>();
        var directory = new FakeAgentDirectory();
        using var drafts = new HandoffDrafts(failures, directory, time);

        drafts.HoldResume("source-1", "agent-1", Draft);
        directory.Rows.AddOrUpdate(LocalRow("agent-1"));
        time.Advance(TimeSpan.FromMinutes(10));

        await Assert.That(drafts.TakeFailed("source-1")).IsNull();
    }

    static AgentRow LocalRow(string id) => AgentRow.FromLocal(
        new AgentStatusDto(id, "agent", "codex", "/repo", "Running",
            FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null, RequesterDisplay: null),
        new RepoIdentity("path:/repo", "repo"));

    /// <summary>A failure that lands before the hold is kept for it even when a row shows up first: a row is no
    /// evidence against a failure already in.</summary>
    [Test]
    public async Task A_failure_before_the_hold_outlives_a_row_that_arrives_before_it() {
        var time = new FakeTimeProvider();
        using var failures = new Subject<LaunchFailure>();
        var directory = new FakeAgentDirectory();
        using var drafts = new HandoffDrafts(failures, directory, time);

        failures.OnNext(new LaunchFailure("agent-1", "resume_failed: runtime_exited"));
        directory.Rows.AddOrUpdate(LocalRow("agent-1"));

        await Assert.That(drafts.HoldResume("source-1", "agent-1", Draft)).IsEqualTo(HandoffHold.AlreadyFailed);
    }

    /// <summary>A row that is already there when the resume is held shows it started, so its expiry returns nothing.</summary>
    [Test]
    public async Task A_row_before_the_hold_counts_as_started() {
        var time = new FakeTimeProvider();
        using var failures = new Subject<LaunchFailure>();
        var directory = new FakeAgentDirectory();
        using var drafts = new HandoffDrafts(failures, directory, time);
        directory.Rows.AddOrUpdate(LocalRow("agent-1"));

        drafts.HoldResume("source-1", "agent-1", Draft);
        await Assert.That(drafts.IsHolding("agent-1")).IsTrue();
        time.Advance(TimeSpan.FromMinutes(10));

        await Assert.That(drafts.TakeFailed("source-1")).IsNull();
        await Assert.That(drafts.IsHolding("agent-1")).IsFalse();
    }

    /// <summary>An expiry's notification queued for the UI thread does nothing once the drafts are disposed, rather than
    /// throwing into whatever runs that queue next.</summary>
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_expiry_notification_queued_past_disposal_does_not_throw() {
        var time = new FakeTimeProvider();
        using var failures = new Subject<LaunchFailure>();
        var drafts = new HandoffDrafts(failures, directory: null, time);
        drafts.HoldResume("source-1", "agent-1", Draft);

        time.Advance(TimeSpan.FromMinutes(10));
        drafts.Dispose();

        await AvaloniaSession.DispatchAsync(() => {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            return 0;
        });
    }

    /// <summary>A silent expiry offers only the hand-off, but only until that resume's agent turns up after all.</summary>
    [Test]
    public async Task A_resume_that_turns_up_after_its_expiry_is_offered_again() {
        var time = new FakeTimeProvider();
        using var failures = new Subject<LaunchFailure>();
        var directory = new FakeAgentDirectory();
        using var drafts = new HandoffDrafts(failures, directory, time);

        drafts.HoldResume("source-1", "agent-1", Draft);
        time.Advance(TimeSpan.FromMinutes(10));
        await Assert.That(drafts.ResumeFailed("source-1")).IsTrue();

        directory.Rows.AddOrUpdate(LocalRow("agent-1"));

        await Assert.That(drafts.ResumeFailed("source-1")).IsFalse();
    }
}
