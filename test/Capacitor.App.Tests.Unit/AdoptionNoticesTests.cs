using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.Services.CliAdoption;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

public class AdoptionNoticesTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    AdoptionRecordStore Store() => new(new ConfigRoot(Tmp.CreateDir("config")));

    /// <summary>A toast fired while no window is on screen is never seen, as when the app starts hidden in the menu bar:
    /// the notice waits for a window, and is recorded as shown only once that window's notifier has it.</summary>
    [Test]
    public async Task A_notice_posted_while_hidden_is_shown_when_the_window_appears() {
        var store    = Store();
        var window   = new BehaviorSubject<IAppNotifier?>(null);
        var notifier = new RecordingNotifier();
        using var notices = new AdoptionNotices(window, store);

        notices.Post("switched:/opt/kcap", "kcap now uses the app's command-line tool.");
        await notices.Settled.WaitAsync(Bounded);

        await Assert.That(store.Load().NoticesShown).IsEmpty();

        window.OnNext(notifier);
        await notices.Settled.WaitAsync(Bounded);

        await Assert.That(notifier.Notified).IsEquivalentTo(["kcap now uses the app's command-line tool."]);
        await Assert.That(store.Load().NoticesShown).IsEquivalentTo(["switched:/opt/kcap"]);

        window.OnNext(null);
        window.OnNext(notifier);
        notices.Post("switched:/opt/kcap", "kcap now uses the app's command-line tool.");
        await notices.Settled.WaitAsync(Bounded);

        await Assert.That(notifier.Notified.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_key_shown_on_an_earlier_launch_is_dropped() {
        var store = Store();
        store.Update(r => r with { NoticesShown = ["left:/opt/kcap:UnsupervisedDaemon"] });
        var notifier = new RecordingNotifier();
        using var notices = new AdoptionNotices(new BehaviorSubject<IAppNotifier?>(notifier), store);

        notices.Post("left:/opt/kcap:UnsupervisedDaemon", "kcap kept the npm install.");
        notices.Post("reported:/opt/kcap", "kcap found the npm install.");
        await notices.Settled.WaitAsync(Bounded);

        await Assert.That(notifier.Notified).IsEquivalentTo(["kcap found the npm install."]);
        await Assert.That(store.Load().NoticesShown).IsEquivalentTo(["left:/opt/kcap:UnsupervisedDaemon", "reported:/opt/kcap"]);
    }

    /// <summary>An outcome that moved on while its notice waited is told as it stands when the window appears.</summary>
    [Test]
    public async Task A_newer_message_for_a_key_still_waiting_replaces_it() {
        var window   = new BehaviorSubject<IAppNotifier?>(null);
        var notifier = new RecordingNotifier();
        using var notices = new AdoptionNotices(window, Store());

        notices.Post("switched:/opt/kcap", "is removed once nothing still uses it");
        notices.Post("switched:/opt/kcap", "was removed");
        window.OnNext(notifier);
        await notices.Settled.WaitAsync(Bounded);

        await Assert.That(notifier.Notified).IsEquivalentTo(["was removed"]);
    }

    /// <summary>A left install's notice names what to change and that the app finishes the switch afterwards; its key
    /// stays the same whatever the cause, so one outcome is told once.</summary>
    [Test]
    public async Task A_left_notice_says_what_finishes_it() {
        var install = new OtherCliInstall(OtherCliInstallKind.Script, "/Users/u/.kcap", "1.0.0", "/Users/u/.kcap/current/bin/kcap",
            "/Users/u/.kcap/current/bin/kcap-daemon", "id", ["/Users/u/.kcap/versions", "/Users/u/.kcap/current", "/Users/u/.kcap/install.json"]);

        var path   = AdoptionNotices.Left(install, LeftReason.OwnFolderOnPath, "/Users/u/.kcap/current/bin");
        var daemon = AdoptionNotices.Left(install, LeftReason.UnsupervisedDaemon, "manual");

        await Assert.That(path).Contains("/Users/u/.kcap/current/bin");
        await Assert.That(path).Contains("open kcap again to finish switching");
        await Assert.That(daemon).Contains("kcap daemon stop --name manual --keep-agents");
        await Assert.That(daemon).Contains("open kcap again to finish switching");
        foreach (var reason in Enum.GetValues<LeftReason>())
            await Assert.That(AdoptionNotices.Left(install, reason, "x")).DoesNotContain("npm uninstall");
        await Assert.That(AdoptionNotices.LeftKey(install, LeftReason.UnsupervisedDaemon)).IsEqualTo("left:/Users/u/.kcap:UnsupervisedDaemon");
    }
}
