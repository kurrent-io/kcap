using System.Reactive.Subjects;
using Capacitor.App.Services;
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
}
