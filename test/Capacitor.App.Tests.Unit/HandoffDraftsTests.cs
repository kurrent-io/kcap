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
    }

    /// <summary>A resume whose agent showed up as a row started: its expiry returns nothing.</summary>
    [Test]
    public async Task An_expired_resume_that_started_returns_nothing() {
        var time = new FakeTimeProvider();
        using var failures = new Subject<LaunchFailure>();
        var directory = new FakeAgentDirectory();
        using var drafts = new HandoffDrafts(failures, directory, time);

        drafts.HoldResume("source-1", "agent-1", Draft);
        directory.Rows.AddOrUpdate(AgentRow.FromLocal(
            new AgentStatusDto("agent-1", "agent", "codex", "/repo", "Running",
                FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null, RequesterDisplay: null),
            new RepoIdentity("path:/repo", "repo")));
        time.Advance(TimeSpan.FromMinutes(10));

        await Assert.That(drafts.TakeFailed("source-1")).IsNull();
    }
}
