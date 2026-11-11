using System.Runtime.Versioning;
using Capacitor.Cli.Core.LocalEdition;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit;

public class RecorderClaimsTests {
    const string Key = "session-1";

    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));

    string Directory => field ??= Tmp.CreateDir("watchers");

    RecorderClaims Claims => field ??= new(Directory, _time);

    /// <summary>Two daemons can share one directory. One that woke after a sleep must neither keep a source the other
    /// took nor keep that one's claim fresh for it, or the claim would outlive the daemon actually holding it.</summary>
    [Test]
    public async Task A_holders_view_beats_and_keeps_only_its_own_claim() {
        var mine  = new RecorderClaims(Directory, _time, "daemon-a");
        var other = new RecorderClaims(Directory, _time, "daemon-b");
        mine.TryTake(Key, "daemon-a", isHosted: false);
        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));
        other.TryTake(Key, "daemon-b", isHosted: false);
        var taken = File.GetLastWriteTimeUtc(mine.ClaimFile(Key));

        _time.Advance(TimeSpan.FromSeconds(10));
        mine.Beat(Key);

        await Assert.That(mine.Keeps(Key)).IsFalse();
        await Assert.That(other.Keeps(Key)).IsTrue();
        await Assert.That(File.GetLastWriteTimeUtc(mine.ClaimFile(Key))).IsEqualTo(taken);

        other.Beat(Key);
        await Assert.That(File.GetLastWriteTimeUtc(mine.ClaimFile(Key))).IsGreaterThan(taken);
    }

    /// <summary>A read that fails once must not stop a recorder with no final drain, so a claim it can't read is kept.</summary>
    [Test]
    public async Task A_holders_view_keeps_a_claim_it_cannot_read_and_no_claim_at_all_is_lost() {
        var mine = new RecorderClaims(Directory, _time, "daemon-a");
        mine.TryTake(Key, "daemon-a", isHosted: false);
        await File.WriteAllTextAsync(mine.ClaimFile(Key), "garbage");

        await Assert.That(mine.Keeps(Key)).IsTrue();

        mine.Release(Key);
        await Assert.That(mine.Keeps(Key)).IsFalse();
    }

    /// <summary>Without a holder any claim keeps a source, stale or not, as a host with one owner always read it.</summary>
    [Test]
    public async Task Claims_with_no_holder_keep_any_claim() {
        Claims.TryTake(Key, "daemon-b", isHosted: false);
        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

        await Assert.That(Claims.Keeps(Key)).IsTrue();
    }

    [Test]
    public async Task A_source_with_no_file_has_no_claim() {
        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(Claims.IsHosted(Key)).IsFalse();
    }

    [Test]
    public async Task A_missing_directory_reads_as_no_claim() {
        var claims = new RecorderClaims(Tmp.PathTo("never-made"), _time);

        await Assert.That(claims.Read(Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(claims.IsHosted(Key)).IsFalse();
    }

    [Test]
    public async Task A_read_of_a_key_no_path_can_hold_does_not_throw() {
        await Assert.That(Claims.Read("bad\0key")).IsEqualTo(RecorderClaimState.None);
        await Assert.That(Claims.IsHosted("bad\0key")).IsFalse();
    }

    [Test]
    public async Task A_claim_is_fresh_up_to_the_grace_and_stale_past_it() {
        await Assert.That(Claims.TryTake(Key, "daemon", isHosted: false)).IsTrue();
        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonFresh);

        _time.Advance(RecorderClaims.Grace);
        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonFresh);

        _time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonStale);
    }

    [Test]
    public async Task A_beat_keeps_a_claim_fresh() {
        Claims.TryTake(Key, "daemon", isHosted: false);

        _time.Advance(RecorderClaims.Grace);
        Claims.Beat(Key);
        _time.Advance(RecorderClaims.Grace);

        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    /// <summary>Time a laptop spends asleep counts: the claim comes out of it stale, and only the holder's
    /// next beat freshens it.</summary>
    [Test]
    public async Task A_sleep_leaves_a_claim_stale_until_its_holder_beats() {
        Claims.TryTake(Key, "daemon", isHosted: false);

        _time.Advance(TimeSpan.FromHours(8));
        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonStale);

        Claims.Beat(Key);
        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    /// <summary>A daemon's beat can be on its way when a hook takes the claim. Bringing the file back would
    /// hand the daemon a source a watcher is already recording.</summary>
    [Test]
    public async Task A_beat_never_brings_back_a_claim_that_went() {
        Claims.TryTake(Key, "daemon", isHosted: false);
        Claims.Release(Key);

        Claims.Beat(Key);

        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.None);
        await Assert.That(System.IO.Directory.GetFiles(Directory)).IsEmpty();
    }

    [Test]
    public async Task A_beat_in_a_missing_directory_is_harmless() {
        var claims = new RecorderClaims(Tmp.PathTo("never-made"), _time);

        claims.Beat(Key);

        await Assert.That(claims.Read(Key)).IsEqualTo(RecorderClaimState.None);
    }

    /// <summary>A clock set back must not keep a dead daemon's claim fresh until it catches up.</summary>
    [Test]
    public async Task A_beat_from_past_the_grace_ahead_of_now_is_stale() {
        var ahead = new RecorderClaims(Directory, new FakeTimeProvider(_time.GetUtcNow() + RecorderClaims.Grace + TimeSpan.FromSeconds(1)));
        ahead.TryTake(Key, "daemon", isHosted: false);

        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonStale);
    }

    [Test]
    public async Task A_released_claim_is_gone_and_releasing_nothing_is_harmless() {
        Claims.TryTake(Key, "daemon", isHosted: false);

        Claims.Release(Key);
        Claims.Release(Key);
        new RecorderClaims(Tmp.PathTo("never-made"), _time).Release(Key);

        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.None);
    }

    [Test]
    public async Task A_fresh_claim_refuses_another_daemon() {
        Claims.TryTake(Key, "daemon-a", isHosted: true);

        await Assert.That(Claims.TryTake(Key, "daemon-b", isHosted: false)).IsFalse();
        await Assert.That(Claims.IsHosted(Key)).IsTrue();
    }

    /// <summary>A daemon that restarts inside the grace finds its own claim and carries on under it.</summary>
    [Test]
    public async Task A_daemon_takes_its_own_fresh_claim_again() {
        Claims.TryTake(Key, "daemon", isHosted: false);

        await Assert.That(Claims.TryTake(Key, "daemon", isHosted: true)).IsTrue();
        await Assert.That(Claims.IsHosted(Key)).IsTrue();
    }

    [Test]
    public async Task A_stale_claim_can_be_taken_by_another_daemon() {
        Claims.TryTake(Key, "daemon-a", isHosted: true);
        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

        await Assert.That(Claims.TryTake(Key, "daemon-b", isHosted: false)).IsTrue();

        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(Claims.IsHosted(Key)).IsFalse();
        await Assert.That(Claims.TryTake(Key, "daemon-a", isHosted: true)).IsFalse();
    }

    [Test]
    public async Task A_claim_says_whether_its_session_is_hosted() {
        Claims.TryTake("hosted", "daemon", isHosted: true);
        Claims.TryTake("terminal", "daemon", isHosted: false);

        await Assert.That(Claims.IsHosted("hosted")).IsTrue();
        await Assert.That(Claims.IsHosted("terminal")).IsFalse();
    }

    [Test]
    public async Task A_take_makes_its_directory() {
        var claims = new RecorderClaims(Tmp.PathTo("made-on-take"), _time);

        await Assert.That(claims.TryTake(Key, "daemon", isHosted: false)).IsTrue();
        await Assert.That(claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    /// <summary>A file where the directory should be is one a daemon cannot recover from here.</summary>
    [Test]
    public async Task A_take_that_cannot_be_written_is_false() {
        var claims = new RecorderClaims(Tmp.CreateFile("in-the-way", ""), _time);

        await Assert.That(claims.TryTake(Key, "daemon", isHosted: false)).IsFalse();
    }

    /// <summary>The Windows trap: a move over a file another process has open is refused. A claim is replaced
    /// while a hook may be reading it, so that has to work.</summary>
    [Test]
    public async Task A_claim_can_be_replaced_while_a_reader_holds_it_open() {
        Claims.TryTake(Key, "daemon-a", isHosted: true);
        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

        using (Reader())
            await Assert.That(Claims.TryTake(Key, "daemon-b", isHosted: false)).IsTrue();

        await Assert.That(Claims.IsHosted(Key)).IsFalse();
    }

    /// <summary>A hook reading the claim must not cost its holder a beat, or a live daemon's claim goes stale.</summary>
    [Test]
    public async Task A_claim_can_be_beaten_while_a_reader_holds_it_open() {
        Claims.TryTake(Key, "daemon", isHosted: false);
        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

        using (Reader()) Claims.Beat(Key);

        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
    }

    /// <summary>A release is how a hook takes a claim, so one a reader could refuse would keep a recorder running.</summary>
    [Test]
    public async Task A_claim_can_be_released_while_a_reader_holds_it_open() {
        Claims.TryTake(Key, "daemon", isHosted: false);

        using (Reader()) Claims.Release(Key);

        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.None);
    }

    FileStream Reader() => new(Claims.ClaimFile(Key), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>A crash after a rename whose content never reached the disk is what would leave a corrupt claim.</summary>
    [Test]
    public async Task A_take_reaches_the_disk_before_it_is_published() {
        var points = new List<string>();

        using (FaultPoints.Arm(points.Add)) Claims.TryTake(Key, "daemon", isHosted: false);

        await Assert.That(points[0]).IsEqualTo($"flush:{Claims.ClaimFile(Key)}");
        await Assert.That(points[1]).IsEqualTo($"rename:{Claims.ClaimFile(Key)}");
    }

    /// <summary>Whatever damaged a claim, reading it as no claim would start a second recorder beside a live daemon,
    /// and reading it as hosted would leave a dead daemon's session to a catch-up that may never come. So it counts
    /// by its age like any claim, and once stale it is a terminal one: a hook takes it, and if the session was hosted
    /// it stays on a watcher, unstamped for the rest of its life but recorded.</summary>
    [Test]
    [Arguments("")]
    [Arguments("hos")]
    [Arguments("hosted")]
    [Arguments("hosted\n")]
    [Arguments("terminal")]
    [Arguments("\nhosted\ndaemon")]
    [Arguments("{\"holder\":\"daemon\"}")]
    [Arguments("\0\0\0\0")]
    public async Task A_corrupt_claim_reads_by_its_age_and_never_as_hosted(string content) {
        Claims.TryTake(Key, "daemon", isHosted: true);
        var beat = File.GetLastWriteTimeUtc(Claims.ClaimFile(Key));
        await File.WriteAllTextAsync(Claims.ClaimFile(Key), content);
        File.SetLastWriteTimeUtc(Claims.ClaimFile(Key), beat);

        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonFresh);
        await Assert.That(Claims.IsHosted(Key)).IsFalse();

        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

        await Assert.That(Claims.Read(Key)).IsEqualTo(RecorderClaimState.DaemonStale);
        await Assert.That(Claims.IsHosted(Key)).IsFalse();
    }

    /// <summary>Nobody can say whose a corrupt claim is, so while it is fresh it may still be someone's, the
    /// daemon that wrote it included. Staleness frees it.</summary>
    [Test]
    public async Task A_fresh_corrupt_claim_refuses_every_daemon_until_it_goes_stale() {
        Claims.TryTake(Key, "daemon", isHosted: false);
        var beat = File.GetLastWriteTimeUtc(Claims.ClaimFile(Key));
        await File.WriteAllTextAsync(Claims.ClaimFile(Key), "garbage");
        File.SetLastWriteTimeUtc(Claims.ClaimFile(Key), beat);

        await Assert.That(Claims.TryTake(Key, "daemon", isHosted: false)).IsFalse();

        _time.Advance(RecorderClaims.Grace + TimeSpan.FromSeconds(1));

        await Assert.That(Claims.TryTake(Key, "daemon", isHosted: true)).IsTrue();
        await Assert.That(Claims.IsHosted(Key)).IsTrue();
    }

    /// <summary>Takes publish by rename, so a reader finds the old claim or the new one and a take leaves
    /// nothing else behind; the pid file a watcher would own is never among what a claim writes.</summary>
    [Test]
    public async Task A_claim_is_one_file_and_never_a_pid_file() {
        var paths = new WatcherPaths(Directory);

        Claims.TryTake(Key, "daemon", isHosted: false);
        Claims.Beat(Key);
        Claims.TryTake(Key, "daemon", isHosted: false);

        await Assert.That(System.IO.Directory.GetFiles(Directory)).IsEquivalentTo([Claims.ClaimFile(Key)]);
        await Assert.That(Claims.ClaimFile(Key)).IsNotEqualTo(paths.PidFile(Key));
    }

    /// <summary>What <c>kcap disable</c> asks, from a terminal that has no daemon named in its environment.</summary>
    [Test]
    public async Task A_claim_names_its_holder() {
        Claims.TryTake(Key, "daemon-a", isHosted: true);

        await Assert.That(Claims.Holder(Key)).IsEqualTo("daemon-a");
    }

    [Test]
    public async Task A_claim_that_cannot_be_read_names_nobody() {
        await File.WriteAllTextAsync(Claims.ClaimFile(Key), "garbage");

        await Assert.That(Claims.Holder(Key)).IsNull();
        await Assert.That(Claims.Holder("no-such-key")).IsNull();
    }

    [Test]
    public async Task A_sessions_keys_are_its_own_and_its_subagents_and_no_others() {
        Claims.TryTake("session-1", "daemon-a", isHosted: true);
        Claims.TryTake("session-1-sub-1", "daemon-a", isHosted: true);
        Claims.TryTake("session-11", "daemon-a", isHosted: true);

        await Assert.That(Claims.KeysOf("session-1")).IsEquivalentTo(["session-1", "session-1-sub-1"]);
    }

    [Test]
    public async Task A_missing_directory_has_no_keys() =>
        await Assert.That(new RecorderClaims(Tmp.PathTo("never-made"), _time).KeysOf(Key)).IsEmpty();

    /// <summary>"No claim" and "couldn't look" are different answers, and <c>kcap disable</c> prints its clean line on the
    /// first.</summary>
    [Test]
    [ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task Keys_that_cannot_be_listed_are_no_answer() {
        Claims.TryTake(Key, "daemon-a", isHosted: true);
        File.SetUnixFileMode(Directory, UnixFileMode.None);

        try {
            await Assert.That(Claims.KeysOf(Key)).IsNull();
        } finally {
            File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
