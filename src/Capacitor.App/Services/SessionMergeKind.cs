namespace Capacitor.App.Services;

internal enum SessionMergeKind {
    HostedLocally,
    HostedRemotely,
    HandingOver,
    FinishedHeld,
    RecordedOnly,
    Restored,
}
