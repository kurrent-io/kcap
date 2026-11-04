using Capacitor.Cli.Core.Auth;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

public static class LocalDaemonTwin {
    /// Exactly-one identity or null (fail open). Server scoping first: no twin when the local
    /// daemon's server is not the app's server. One identity listed twice, as a reconnect that
    /// replaces a connection can leave it, is still one.
    public static (string OwnerUserId, string DaemonName)? Find(
            IReadOnlyList<DaemonInfo> daemons, string? localMachineId, string localDaemonName,
            string? localServerUrl, string? appServerUrl) {
        if (localMachineId is null) return null;
        if (!ServerIdentity.SameServer(localServerUrl, appServerUrl)) return null;

        (string, string)? found = null;
        foreach (var d in daemons) {
            if (d.MachineId != localMachineId || d.Name != localDaemonName || d.OwnerUserId is null) continue;
            if (found is { } seen && seen != (d.OwnerUserId, d.Name)) return null;
            found = (d.OwnerUserId, d.Name);
        }
        return found;
    }

    /// A twin proven earlier, while the registry lists nothing that contradicts it: no daemon of
    /// this machine under the name with another owner, and no other machine in its slot.
    public static (string OwnerUserId, string DaemonName)? Retained(
            (string OwnerUserId, string DaemonName)? proven, IReadOnlyList<DaemonInfo> daemons, string? localMachineId,
            string localDaemonName, string? localServerUrl, string? appServerUrl) {
        if (proven is not { } twin || localMachineId is null || twin.DaemonName != localDaemonName) return null;
        if (!ServerIdentity.SameServer(localServerUrl, appServerUrl)) return null;

        foreach (var d in daemons) {
            if (d.Name != localDaemonName || d.OwnerUserId is null) continue;
            var sameSlot = d.OwnerUserId == twin.OwnerUserId;
            if (sameSlot != (d.MachineId == localMachineId)) return null;
        }
        return twin;
    }
}
