namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>What a SessionEnd hook later than the daemon's grace reads, so it posts no second end.</summary>
public class SessionEndRecordTests {
    const string Session = "0123456789abcdef0123456789abcdef";

    static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [TempDir] public required TempDir Tmp { get; init; }

    WatcherPaths Paths => field ??= new(Tmp.CreateDir("watchers"));

    [Test]
    public async Task A_fresh_record_is_taken_once() {
        SessionEndRecord.Write(Paths, Session, Now);

        await Assert.That(SessionEndRecord.TryTake(Paths, Session, Now + TimeSpan.FromSeconds(30))).IsTrue();
        await Assert.That(SessionEndRecord.TryTake(Paths, Session, Now + TimeSpan.FromSeconds(31))).IsFalse();
    }

    /// <summary>A session resumed and ended again later owes its own end.</summary>
    [Test]
    public async Task A_record_past_its_window_holds_nothing_back_and_goes() {
        SessionEndRecord.Write(Paths, Session, Now);

        await Assert.That(SessionEndRecord.TryTake(Paths, Session, Now + SessionEndRecord.Window + TimeSpan.FromSeconds(1))).IsFalse();
        await Assert.That(Directory.GetFiles(Paths.Directory)).IsEmpty();
    }

    [Test]
    public async Task No_record_is_no_end_posted() =>
        await Assert.That(SessionEndRecord.TryTake(Paths, Session, Now)).IsFalse();

    /// <summary>Hooks spell a session with dashes or without; the daemon writes the dashless one.</summary>
    [Test]
    public async Task A_record_is_found_however_the_hook_spells_the_session() {
        SessionEndRecord.Write(Paths, Session, Now);

        await Assert.That(SessionEndRecord.TryTake(Paths, "01234567-89ab-cdef-0123-456789abcdef", Now)).IsTrue();
    }
}
