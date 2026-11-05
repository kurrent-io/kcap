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

    /// <summary>The menu bar item that runs the switch again for an install a prompt or a deletion left.</summary>
    internal const string FinishSwitching = "Finish switching to the app's kcap…";

    internal static string SwitchedKey(OtherCliInstall install) => "switched:" + install.Root;

    /// <param name="cause">Part of the key for a reason found again on each launch, whose notice names its cause: another
    /// cause is another outcome to tell.</param>
    internal static string LeftKey(OtherCliInstall install, LeftReason reason, string? cause) =>
        AdoptionPreflight.Rederived(reason) && cause is not null ? $"left:{install.Root}:{reason}:{cause}" : $"left:{install.Root}:{reason}";

    internal static string ReportedKey(OtherCliInstall install) => "reported:" + install.Root;

    internal static string Switched(OtherCliInstall install, bool removed) =>
        $"kcap now uses the app's command-line tool. The {Name(install)} {(removed ? "was removed." : "is removed once nothing still uses it.")}";

    /// <param name="cause">The PATH folder or daemon that left it, when the plan named one.</param>
    internal static string Left(OtherCliInstall install, LeftReason reason, string? cause) =>
        $"kcap kept the {Name(install)} at {install.Root}: {Why(reason, cause)}";

    internal static string Reported(OtherCliInstall install) =>
        $"kcap found the {Name(install)} at {install.Root} and left it in place. A terminal may run it instead of the app's command-line tool.";

    internal static string Name(OtherCliInstall install) =>
        $"{(install.Kind == OtherCliInstallKind.Npm ? "npm" : "script")} install of kcap {install.Version}";

    // Each reason says what finishes the switch: a change the user makes before kcap next opens, or the menu bar item
    // that runs it again. Never an uninstall on its own, which would leave no kcap on that PATH.
    static string Why(LeftReason reason, string? cause) => reason switch {
        LeftReason.OwnFolderOnPath =>
            $"your PATH lists {cause ?? "a folder inside it"}, so removing the install would leave no kcap there. "
          + "Remove that folder from PATH in your shell profile, then open kcap again to finish switching.",
        LeftReason.UnsupervisedDaemon =>
            $"the kcap daemon {(cause is null ? "" : $"'{cause}' ")}runs from it, and its service did not start it. "
          + $"Stop it with kcap daemon stop{(cause is null ? "" : $" --name {cause}")} --keep-agents, then open kcap again to finish switching. "
          + "A daemon older than 1.3.1 ends its agents when it stops whatever the flag says, so let their work finish first.",
        LeftReason.PromptCancelled =>
            $"the administrator prompt was cancelled. To finish, choose {FinishSwitching} in the menu bar.",
        LeftReason.DeletionCutShort =>
            $"removing it was cut short, and only an administrator can remove the rest. To finish, choose {FinishSwitching} in the menu bar.",
        LeftReason.SharedInstallPrivateApp => "it serves every account on this Mac, and this app is installed for you alone.",
        _                                  => "it could not be switched.",
    };
}
