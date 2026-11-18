namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>The one record a SessionEnd hook and the daemon race for, so between them one end of a process's run reaches
/// the server.</summary>
public class SessionEndRecordTests {
    const string Session = "0123456789abcdef0123456789abcdef";

    static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    static readonly ProcessIncarnation First  = new(4242, "mac:1:2");
    static readonly ProcessIncarnation Second = new(4343, "mac:3:4");

    [TempDir] public required TempDir Tmp { get; init; }

    WatcherPaths Paths => field ??= new(Tmp.CreateDir("watchers"));

    string Record => Directory.GetFiles(Paths.Directory).Single();

    [Test]
    public async Task The_first_claim_wins_and_a_second_waits_on_it() {
        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, First, Now)).IsEqualTo(SessionEndTurn.Yours);
        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, First, Now + TimeSpan.FromSeconds(5))).IsEqualTo(SessionEndTurn.Pending);
        await Assert.That(SessionEndRecord.Exists(Paths, Session, First)).IsTrue();
    }

    /// <summary>A claimer killed before its post never marks it, and the session would get no end. Once the claim is
    /// older than any claimer takes, whoever comes next posts it.</summary>
    [Test]
    public async Task A_claim_never_marked_posted_is_taken_over_once_stale() {
        SessionEndRecord.TryClaim(Paths, Session, First, Now);

        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, First, Now + SessionEndRecord.TakeOver)).IsEqualTo(SessionEndTurn.Yours);
        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, First, Now + SessionEndRecord.TakeOver + TimeSpan.FromSeconds(1))).IsEqualTo(SessionEndTurn.Pending);
    }

    [Test]
    public async Task An_end_marked_posted_is_never_taken_over() {
        SessionEndRecord.TryClaim(Paths, Session, First, Now);
        SessionEndRecord.MarkPosted(Paths, Session, First, Now);

        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, First, Now + TimeSpan.FromDays(1))).IsEqualTo(SessionEndTurn.Done);
    }

    /// <summary>A run whose session went on in another process owes no end, and its late hook posts none.</summary>
    [Test]
    public async Task A_settled_end_is_never_posted() {
        SessionEndRecord.Settle(Paths, Session, First, Now);

        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, First, Now + TimeSpan.FromDays(1))).IsEqualTo(SessionEndTurn.Done);
    }

    /// <summary>A follow after the end went out leaves the record saying posted.</summary>
    [Test]
    public async Task Settling_keeps_an_end_already_posted() {
        SessionEndRecord.TryClaim(Paths, Session, First, Now);
        SessionEndRecord.MarkPosted(Paths, Session, First, Now);

        SessionEndRecord.Settle(Paths, Session, First, Now);

        await Assert.That(File.ReadAllText(Record)).StartsWith("posted\n");
    }

    /// <summary>Marking posted names a claim someone made, and makes none.</summary>
    [Test]
    public async Task Marking_an_end_nobody_claimed_posted_leaves_no_record() {
        SessionEndRecord.MarkPosted(Paths, Session, First, Now);

        await Assert.That(SessionEndRecord.Exists(Paths, Session, First)).IsFalse();
    }

    /// <summary>A record left empty, as one whose write failed after the create is, says nothing of a post, so it is taken
    /// over at once.</summary>
    [Test]
    public async Task A_record_that_cannot_be_read_is_taken_over() {
        SessionEndRecord.TryClaim(Paths, Session, First, Now);
        File.WriteAllText(Record, "");

        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, First, Now)).IsEqualTo(SessionEndTurn.Yours);
    }

    /// <summary>A resumed run is another process, and owes an end of its own however soon it ends.</summary>
    [Test]
    public async Task A_claim_holds_back_only_the_process_it_names() {
        SessionEndRecord.TryClaim(Paths, Session, First, Now);

        await Assert.That(SessionEndRecord.Exists(Paths, Session, Second)).IsFalse();
        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, Second, Now)).IsEqualTo(SessionEndTurn.Yours);
    }

    /// <summary>A later process given the same pid is not the one that ended.</summary>
    [Test]
    public async Task A_pid_reused_under_another_start_token_is_another_process() {
        SessionEndRecord.TryClaim(Paths, Session, First, Now);

        await Assert.That(SessionEndRecord.TryClaim(Paths, Session, First with { StartToken = "mac:9:9" }, Now)).IsEqualTo(SessionEndTurn.Yours);
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

        await Assert.That(SessionEndRecord.TryClaim(blocked, Session, First, Now)).IsEqualTo(SessionEndTurn.Yours);
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
    public async Task The_record_is_owner_only_through_every_state() {
        SessionEndRecord.TryClaim(Paths, Session, First, Now);
        var claimed = File.GetUnixFileMode(Record);
        SessionEndRecord.MarkPosted(Paths, Session, First, Now);

        await Assert.That(claimed).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(File.GetUnixFileMode(Record)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
