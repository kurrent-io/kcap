namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>The one record a SessionEnd hook and the daemon race for, so between them one end of a process's run is
/// posted.</summary>
public class SessionEndRecordTests {
    const string Session = "0123456789abcdef0123456789abcdef";

    static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    static readonly ProcessIncarnation First  = new(4242, "mac:1:2");
    static readonly ProcessIncarnation Second = new(4343, "mac:3:4");

    [TempDir] public required TempDir Tmp { get; init; }

    WatcherPaths Paths => field ??= new(Tmp.CreateDir("watchers"));

    [Test]
    public async Task Only_the_first_claim_of_a_processes_end_wins() {
        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, First, Now)).IsTrue();
        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, First, Now)).IsFalse();
        await Assert.That(SessionEndRecord.Exists(Paths, Session, First)).IsTrue();
    }

    /// <summary>A resumed run is another process, and owes an end of its own however soon it ends.</summary>
    [Test]
    public async Task A_claim_holds_back_only_the_process_it_names() {
        SessionEndRecord.TryClaim(Paths, Session, First, Now);

        await Assert.That(SessionEndRecord.Exists(Paths, Session, Second)).IsFalse();
        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, Second, Now)).IsTrue();
    }

    /// <summary>A later process given the same pid is not the one that ended.</summary>
    [Test]
    public async Task A_pid_reused_under_another_start_token_is_another_process() {
        SessionEndRecord.TryClaim(Paths, Session, First, Now);

        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, First with { StartToken = "mac:9:9" }, Now)).IsTrue();
    }

    [Test]
    public async Task Any_spelling_of_the_session_names_the_same_record() {
        SessionEndRecord.TryClaim(Paths, "01234567-89ab-cdef-0123-456789abcdef", First, Now);

        await Assert.That(SessionEndRecord.Exists(Paths, Session, First)).IsTrue();
    }

    /// <summary>A directory the claim can't be written to leaves the caller to post: one end too many beats none.</summary>
    [Test]
    public async Task A_record_that_cannot_be_written_leaves_the_caller_to_post() {
        var blocked = new WatcherPaths(Tmp.CreateFile("not-a-directory", ""));

        await Assert.That(SessionEndRecord.TryClaim(blocked, Session, First, Now)).IsTrue();
    }

    /// <summary>A record past its retention goes, unless its session still waits on the daemon's decision, which a
    /// daemon down that long makes at its next boot.</summary>
    [Test]
    public async Task Pruning_spares_a_session_whose_end_is_still_pending() {
        SessionEndRecord.TryClaim(Paths, Session, First, Now);
        SessionEndRecord.TryClaim(Paths, "fedcba9876543210fedcba9876543210", First, Now);
        foreach (var record in Directory.GetFiles(Paths.Directory)) File.SetLastWriteTimeUtc(record, (Now - SessionEndRecord.Retention - TimeSpan.FromDays(1)).UtcDateTime);

        SessionEndRecord.Prune(Paths, Now, [Session]);

        await Assert.That(SessionEndRecord.Exists(Paths, Session, First)).IsTrue();
        await Assert.That(SessionEndRecord.Exists(Paths, "fedcba9876543210fedcba9876543210", First)).IsFalse();
    }

    [Test]
    public async Task Pruning_keeps_a_record_inside_its_retention() {
        SessionEndRecord.TryClaim(Paths, Session, First, Now);

        SessionEndRecord.Prune(Paths, DateTimeOffset.UtcNow, []);

        await Assert.That(SessionEndRecord.Exists(Paths, Session, First)).IsTrue();
    }

    [Test]
    [ExcludeOn(TUnit.Core.Enums.OS.Windows)]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task The_record_is_owner_only() {
        SessionEndRecord.TryClaim(Paths, Session, First, Now);

        await Assert.That(File.GetUnixFileMode(Directory.GetFiles(Paths.Directory).Single())).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
