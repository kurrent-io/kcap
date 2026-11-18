using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// The last few closed transcripts: bounded, most recently opened kept, and forgotten when access is lost.
public class RemoteTranscriptCacheTests {
    static RemoteTranscriptSnapshot Snapshot(long position = 9) => new([], position, 0, 0, new ScheduledPrompts());

    [Test]
    public async Task The_least_recently_opened_session_goes_when_a_sixth_is_kept() {
        using var subject = new BehaviorSubject<string?>("u1");
        using var access = new Subject<(string, SessionAccessState)>();
        using var cache = new RemoteTranscriptCache(subject, access);
        for (var i = 0; i < RemoteTranscriptCache.Capacity; i++) cache.Put($"s{i}", Snapshot());
        // Opening s0 again makes it the most recent; s1 is then the oldest.
        cache.Put("s0", cache.Take("s0")!);

        cache.Put("s5", Snapshot());

        await Assert.That(cache.Count).IsEqualTo(RemoteTranscriptCache.Capacity);
        await Assert.That(cache.Contains("s1")).IsFalse();
        await Assert.That(cache.Contains("s0")).IsTrue();
        await Assert.That(cache.Contains("s5")).IsTrue();
    }

    [Test]
    public async Task A_taken_snapshot_is_handed_out_once() {
        using var subject = new BehaviorSubject<string?>("u1");
        using var access = new Subject<(string, SessionAccessState)>();
        using var cache = new RemoteTranscriptCache(subject, access);
        cache.Put("s1", Snapshot(position: 42));

        await Assert.That(cache.Take("s1")!.Position).IsEqualTo(42);
        await Assert.That(cache.Take("s1")).IsNull();
    }

    [Test]
    public async Task A_refused_session_is_forgotten_and_others_stay() {
        using var subject = new BehaviorSubject<string?>("u1");
        using var access = new Subject<(string, SessionAccessState)>();
        using var cache = new RemoteTranscriptCache(subject, access);
        cache.Put("s1", Snapshot());
        cache.Put("s2", Snapshot());

        access.OnNext(("s1", SessionAccessState.Unavailable));
        await Assert.That(cache.Contains("s1")).IsTrue();
        access.OnNext(("s1", SessionAccessState.Denied));

        await Assert.That(cache.Contains("s1")).IsFalse();
        await Assert.That(cache.Contains("s2")).IsTrue();
    }

    [Test]
    public async Task A_change_of_account_forgets_every_session() {
        using var subject = new BehaviorSubject<string?>("u1");
        using var access = new Subject<(string, SessionAccessState)>();
        using var cache = new RemoteTranscriptCache(subject, access);
        cache.Put("s1", Snapshot());

        subject.OnNext(null);

        await Assert.That(cache.Count).IsEqualTo(0);
    }
}
