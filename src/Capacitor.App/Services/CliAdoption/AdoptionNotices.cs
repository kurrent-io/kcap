namespace Capacitor.App.Services.CliAdoption;

/// <summary>Tells the user each takeover outcome once. A toast fired while no window is on screen is never seen, so a
/// message waits until a window's notifier is offered, and its key is recorded as shown only after that notifier has
/// it: an app started hidden in the menu bar tells it when its window first appears, on this launch or a later one.</summary>
internal sealed class AdoptionNotices : IDisposable {
    readonly AdoptionRecordStore _store;
    readonly Lock _lock = new();
    readonly List<(string Key, string Message)> _held = [];
    readonly IDisposable _subscription;
    IAppNotifier? _notifier;
    Task _settled = Task.CompletedTask;

    /// <param name="notifier">The notifier of a window that is on screen, or null while there is none.</param>
    public AdoptionNotices(IObservable<IAppNotifier?> notifier, AdoptionRecordStore store) {
        _store = store;
        _subscription = notifier.Subscribe(next => {
            lock (_lock) _notifier = next;
            Flush();
        });
    }

    /// <summary>Completes once every message posted so far has been shown or left waiting for a window.</summary>
    internal Task Settled {
        get { lock (_lock) return _settled; }
    }

    /// <summary>A key already shown is dropped. A newer message for a key that is still waiting replaces the older one,
    /// so the user reads the outcome as it stands.</summary>
    public void Post(string outcomeKey, string message) {
        lock (_lock) {
            var at = _held.FindIndex(h => h.Key == outcomeKey);
            if (at >= 0) _held[at] = (outcomeKey, message);
            else _held.Add((outcomeKey, message));
        }
        Flush();
    }

    public void Dispose() => _subscription.Dispose();

    // The window's notifier arrives on the UI thread, and the record is a file: shown in order, off that thread.
    void Flush() {
        lock (_lock) _settled = _settled.ContinueWith(_ => Show(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    void Show() {
        try {
            IAppNotifier? notifier;
            (string Key, string Message)[] held;
            lock (_lock) {
                notifier = _notifier;
                held     = [.. _held];
            }
            if (notifier is null || held.Length == 0) return;

            var shown = _store.Load().NoticesShown;
            foreach (var (key, message) in held) {
                if (!shown.Contains(key)) {
                    notifier.Notify(message);
                    _store.Update(r => r.NoticesShown.Contains(key) ? r : r with { NoticesShown = [.. r.NoticesShown, key] });
                }
                lock (_lock) _held.RemoveAll(h => h.Key == key);
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not show a notice about the old kcap install: {ex.Message}");
        }
    }

    internal static string SwitchedKey(OtherCliInstall install) => "switched:" + install.Root;

    internal static string LeftKey(OtherCliInstall install, LeftReason reason) => $"left:{install.Root}:{reason}";

    internal static string ReportedKey(OtherCliInstall install) => "reported:" + install.Root;

    internal static string Switched(OtherCliInstall install, bool removed) =>
        $"kcap now uses the app's command-line tool. The {Name(install)} {(removed ? "was removed." : "is removed once nothing still uses it.")}";

    internal static string Left(OtherCliInstall install, LeftReason reason) =>
        $"kcap kept the {Name(install)} at {install.Root}: {Why(reason)}.";

    internal static string Reported(OtherCliInstall install) =>
        $"kcap found the {Name(install)} at {install.Root} and left it in place. A terminal may run it instead of the app's command-line tool.";

    static string Name(OtherCliInstall install) =>
        $"{(install.Kind == OtherCliInstallKind.Npm ? "npm" : "script")} install of kcap {install.Version}";

    static string Why(LeftReason reason) => reason switch {
        LeftReason.OwnFolderOnPath         => "your PATH names a folder inside it, and removing it would leave no kcap on that PATH",
        LeftReason.UnsupervisedDaemon      => "a kcap daemon its service did not start runs from it",
        LeftReason.PromptCancelled         => "the administrator prompt was cancelled",
        LeftReason.DeletionCutShort        => "removing it was cut short",
        LeftReason.SharedInstallPrivateApp => "it serves every account on this Mac, and this app is installed for you alone",
        _                                  => "it could not be switched",
    };
}
