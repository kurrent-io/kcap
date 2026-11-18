using System.Reactive.Linq;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services;

/// The coding tools this machine found after kcap was set up. Polled because nothing announces an install, and
/// VS Code creates Copilot's data on its first launch, before the user can sign in and chat.
public static class NewHarnessesFeed {
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public static IObservable<NewHarnesses?> Poll(HarnessRegistry harnesses, HarnessOfferStore offers, TimeProvider time) =>
        Observable.Timer(TimeSpan.Zero, Interval)
            .Select(_ => Observable.FromAsync(ct => Task.Run(() => Read(harnesses, offers, time), ct)).Catch(Observable.Return<NewHarnesses?>(null)))
            .Concat();

    /// Null when there is nothing to say. A tool that runs inside another app is named with it, as VS Code Copilot.
    public static NewHarnesses? Read(HarnessRegistry harnesses, HarnessOfferStore offers, TimeProvider time) {
        var found = HarnessNudge.NeverOffered(harnesses, offers.Load());
        if (found.Count == 0) return null;

        return new(
            [.. found.Select(h => harnesses.Detect(h.Id).HostApp is { } host ? $"{host} {h.Label}" : h.Label)],
            () => Task.Run(() => offers.StampOffered(found.Select(h => h.Id), time.GetUtcNow())));
    }
}
