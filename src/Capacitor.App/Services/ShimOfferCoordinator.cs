using System.Reactive.Subjects;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services;

/// The once-ever shim offer + the "Install command-line tool…" tray item it shares a
/// code path with. Waits for the SAME startup-phase signal the lifecycle controller exposes
/// (`PhaseClosed`, any path — an immediate `Connected` releases this exactly like a completed
/// `daemon_unreachable` branch) before probing anything, so the offer dialog can never race the
/// startup matrix. `ConfirmAsync` is routed through the SAME `ILifecycleSurface` the skew/repair
/// dialogs use — its own `SemaphoreSlim(1,1)` already guarantees this offer never stacks over one
/// of those, so no second serialization lives here.
///
/// A no-op where the OS has no installer (<see cref="CliPathInstallers.Create"/> returned null):
/// the target is forced null at construction, so this degrades exactly like "nothing to link"
/// everywhere below.
public sealed class ShimOfferCoordinator {
    readonly Task _phaseClosed;
    readonly ILoginShellProbe _probe;
    readonly ICliPathInstaller? _installer;
    readonly IAppStateStore _store;
    readonly ILifecycleSurface _surface;
    readonly string? _target;
    readonly CancellationToken _lifetime;
    // Skips only the once-ever auto-offer dialog: Offerable, a manual install and a claimed restore still work.
    readonly bool _autoOfferSuppressed;
    readonly Func<bool> _autoOfferHeld;
    readonly bool _restoreClaimed;
    readonly Task<string?> _pendingLink;
    // The warning this coordinator last posted, taken back once an install succeeds.
    string? _warning;

    readonly BehaviorSubject<bool> _offerable = new(false);

    /// <param name="target">
    /// The resolved ABSOLUTE CLI path (CliResolver), or null when only the bare "kcap" command
    /// name resolved — there is nothing to link, so the offer and the menu item both stay off for
    /// the whole run.
    /// </param>
    /// <param name="autoOfferHeld">Read once the phase closes: true holds the auto-offer for a later launch without
    /// using it up, as while the CLI takeover has found an install of its own to switch.</param>
    /// <param name="restoreClaimed">Put back, without asking again, an install already claimed and never declined.
    /// Only where installing cannot conflict or edit a file of theirs, which is the Windows user PATH.</param>
    public ShimOfferCoordinator(
            Task phaseClosed, ILoginShellProbe probe, ICliPathInstaller? installer, IAppStateStore store,
            ILifecycleSurface surface, string? target, CancellationToken lifetime, bool autoOfferSuppressed = false,
            Task<string?>? pendingLink = null, Func<bool>? autoOfferHeld = null, bool restoreClaimed = false) {
        _phaseClosed          = phaseClosed;
        _probe                = probe;
        _installer            = installer;
        _store                = store;
        _surface              = surface;
        _target               = installer is null ? null : target;
        _lifetime             = lifetime;
        _autoOfferSuppressed  = autoOfferSuppressed;
        _pendingLink          = pendingLink ?? Task.FromResult<string?>(null);
        _autoOfferHeld        = autoOfferHeld ?? (() => false);
        _restoreClaimed       = restoreClaimed;
    }

    /// True while the tray's "Install command-line tool…" item should show: applicable (an
    /// absolute link target exists) but absent (the probe positively found no `kcap` on the
    /// terminal PATH) — independent of whether the once-ever auto-offer itself ran.
    public IObservable<bool> Offerable => _offerable;

    /// Call once, after subscribing to `Offerable` — mirrors DaemonLifecycleController.Start's
    /// subscribe-before-run shape. Fire-and-forget: every path through RunAsync is exception-safe.
    public void Start() => _ = RunAsync();

    /// The tray menu item's click handler: runs the SAME install path as an accepted
    /// offer, regardless of whether this run already offered or the user already declined — a
    /// manual retry is always allowed.
    public Task RunManualInstallAsync() => RunInstallAsync();

    async Task RunAsync() {
        try {
            await _phaseClosed.ConfigureAwait(false);
            // A probe before the setup wizard's link settles would offer what is about to exist.
            var unfinished = await AwaitPendingLinkAsync().ConfigureAwait(false);
            if (_target is null) return; // nothing to link — offer and menu item both stay off

            var onPath = await _probe.KcapOnPathAsync(_lifetime).ConfigureAwait(false);
            if (onPath == true) return;
            // Said here because setup may never have shown it, and the preflight below can return
            // without a dialog.
            if (unfinished is not null) await Status(unfinished).ConfigureAwait(false);
            if (onPath is null) return; // unknown → never auto-offer, item stays hidden

            _offerable.OnNext(true); // applicable-but-absent — the menu item is live from here on

            // A restore asks nothing, so a suppressed dialog does not hold it back.
            if (_autoOfferSuppressed && !_restoreClaimed) return;
            if (_autoOfferHeld()) return;

            var state = await _store.LoadAsync().ConfigureAwait(false);
            if (state.ShimDenied) return;
            if (state.ShimOffered) {
                // The Windows uninstaller takes the folder off the user PATH and keeps the settings holding
                // this claim, so a reinstall finds the install claimed and gone while its hooks still need it.
                if (_restoreClaimed) await RunInstallAsync().ConfigureAwait(false);
                return;
            }
            if (_autoOfferSuppressed) return; // item + manual install still work; only the dialog never fires

            var preflight = _installer!.Preflight(_target);
            if (preflight == ShimPreflight.Conflict) return; // no auto-offer; the menu item still lets the user try
            if (preflight == ShimPreflight.AlreadyInstalled) {
                await ClaimOfferedAsync().ConfigureAwait(false);
                return;
            }

            // Claim-before-show: persisted BEFORE ConfirmAsync so a crash while the dialog is
            // open still suppresses a re-offer next run. A persist failure still proceeds — this
            // run never re-checks AppState again, so there is nothing left here to re-offer.
            await ClaimOfferedAsync().ConfigureAwait(false);

            var prompt = new LifecyclePrompt(LifecyclePrompt.KindShim, null, null, false, _installer.Disclosure);
            var accepted = await _surface.ConfirmAsync(prompt, _lifetime).ConfigureAwait(false);
            if (!accepted) {
                await ClaimDeniedAsync().ConfigureAwait(false);
                return;
            }

            await RunInstallAsync().ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // shutdown before the offer completed
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: shim offer failed unexpectedly: {ex.Message}");
        }
    }

    async Task RunInstallAsync() {
        try {
            if (_target is null || _installer is null) return; // defensive — the menu item is never shown without one
            await AwaitPendingLinkAsync().ConfigureAwait(false);
            var result = await _installer.InstallAsync(_target, _lifetime).ConfigureAwait(false);
            await SurfaceResultAsync(result).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // shutdown mid-install
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: shim install failed unexpectedly: {ex.Message}");
        }
    }

    Task SurfaceResultAsync(ShimResult result) => result.Outcome switch {
        // InstalledButNotOnPath keeps the item offerable: kcap is still absent from the terminal
        // PATH. Success posts no line — the status lane is the launcher's warning banner, and
        // nothing clears it while the daemon stays connected.
        ShimOutcome.Installed => Installed(),
        ShimOutcome.InstalledButNotOnPath or ShimOutcome.Failed => Status(InstallFailureText(result)),
        _ => Task.CompletedTask,
    };

    internal static string InstallFailureText(ShimResult result) {
        Console.Error.WriteLine($"kcap: terminal command setup incomplete: {result.Detail}");
        var folder = result.Folder ?? "the folder that contains kcap";
        return result.PathIssue switch {
            ShimPathIssue.InvalidTarget => "kcap’s installation path can’t be added to PATH. Move kcap to a folder with a simple name and try again.",
            ShimPathIssue.UnsupportedShell => $"kcap couldn’t configure this shell automatically. Add {folder} to your shell’s PATH, then open a new terminal.",
            ShimPathIssue.ExistingCommand => $"A command already exists at {Path.Combine(folder, "kcap")}. Move that command if you want to use this copy, then try again.",
            ShimPathIssue.LinkFolderOccupied => $"kcap couldn’t create the terminal command because {folder} is a file. Move the file, then try again.",
            ShimPathIssue.LinkFailed => $"kcap couldn’t create the terminal command in {folder}. Check that you can write to that folder and try again.",
            ShimPathIssue.ShellProfileNotUpdated => $"kcap is linked in {folder}, but its shell settings couldn’t be updated. Add {folder} to your shell’s PATH and open a new terminal.",
            ShimPathIssue.FolderMissingFromPath => $"kcap is linked in {folder}, but that folder is missing from PATH. Add it to your shell’s PATH and open a new terminal.",
            ShimPathIssue.AnotherCommandFirst => $"Your shell finds another kcap before the one in {folder}. Move {folder} earlier in your shell’s PATH and open a new terminal.",
            ShimPathIssue.LinkUnverified => $"kcap is linked in {folder}, but the app couldn’t verify which copy your shell uses. Open a new terminal and run kcap --version.",
            ShimPathIssue.UserPathNotUpdated => $"kcap couldn’t update your user PATH. Open Windows Environment Variables and add {folder} to your user PATH.",
            ShimPathIssue.UserPathUnverified => $"{folder} is on your user PATH, but kcap couldn’t verify the terminal command. Open a new terminal and run kcap --version.",
            _ when result.Outcome == ShimOutcome.InstalledButNotOnPath => "The terminal can’t find the installed command yet. Check your shell’s PATH, then open a new terminal.",
            _ => "Could not finish installing the terminal command. Try again.",
        };
    }

    Task Installed() {
        _offerable.OnNext(false);
        if (_warning is { } warning) _surface.Withdraw(warning);
        _warning = null;
        return Task.CompletedTask;
    }

    Task<string?> AwaitPendingLinkAsync() => _pendingLink.WaitAsync(_lifetime);

    Task Status(string message) {
        _warning = message;
        _surface.Status(message);
        return Task.CompletedTask;
    }

    Task<bool> ClaimOfferedAsync() => _store.UpdateAsync(s => s.ShimOffered ? s : s with { ShimOffered = true });
    Task<bool> ClaimDeniedAsync()  => _store.UpdateAsync(s => s.ShimDenied  ? s : s with { ShimDenied  = true });
}
