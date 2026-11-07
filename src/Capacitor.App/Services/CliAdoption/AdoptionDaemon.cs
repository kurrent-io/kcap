using Capacitor.App.Services.Mutation;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>The daemon the app attaches to, once its graph exists. <paramref name="Request"/> names it for the repoint;
/// it is null when no server is configured.</summary>
internal sealed record AdoptionDaemon(
    MutationRequest? Request, Func<DaemonActivity?> Activity, ExpectedDaemonRestart Expected, Func<Task> Reattach);
