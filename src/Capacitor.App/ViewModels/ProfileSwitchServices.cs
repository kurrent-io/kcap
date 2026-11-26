using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// What the Profiles tab needs to move this app and its daemon to another profile.
/// <param name="BoundDaemonName">The daemon this app attached to at startup.</param>
/// <param name="NameReplies">Whether a daemon answers a one-shot hello under a name.</param>
/// <param name="ServiceStatus">A daemon name's service state; null when it could not be read.</param>
/// <param name="CanActivate">Whether the CLI the lane would run names every flag the request emits.</param>
/// <param name="RestartRequired">The lane refuses further mutations until the app restarts.</param>
/// <param name="ProfileOverridden">KCAP_PROFILE is set, and would outrank the selection on relaunch.</param>
public sealed record ProfileSwitchServices(
    string BoundDaemonName,
    Func<MutationRequest, CancellationToken, Task<MutationOutcome>> RunMutation,
    Func<string, CancellationToken, Task<bool>> Confirm,
    Func<CancellationToken, Task<bool>> Relaunch,
    Task StartupSettled,
    Func<string, CancellationToken, Task<bool>> NameReplies,
    Func<string, CancellationToken, Task<ServiceSnapshot?>> ServiceStatus,
    Func<MutationRequest, CancellationToken, Task<bool>> CanActivate,
    Func<bool> RestartRequired,
    bool ProfileOverridden,
    bool PlatformSupported);
