using System.Diagnostics.CodeAnalysis;
using System.Reactive.Subjects;
using Capacitor.Client;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.Tests.Helpers;

/// Scripted IDaemonClientService — subject-backed Status/Snapshots/Agents — shared by
/// MainWindowViewModelTests and MainWindowSmokeTests so both drive exact event sequences
/// without a real daemon.
[SuppressMessage("Design", "CA1001", Justification = "A fake's subjects live for one test.")]
public sealed class FakeDaemonClientService : IDaemonClientService {
    public BehaviorSubject<AttachStatus> StatusSubject { get; } = new(new(AttachState.Connecting, null, null));
    public ReplaySubject<DaemonStatusDto> SnapshotsSubject { get; } = new(1);

    public IObservable<AttachStatus> Status => StatusSubject;
    public IObservable<DaemonStatusDto> Snapshots => SnapshotsSubject;
    public SourceCache<AgentStatusDto, string> Agents { get; } = new(a => a.Id);
    public SourceCache<PendingLaunchDto, string> Pending { get; } = new(p => p.Id);
    public string DaemonName { get; set; } = "daemon-a";

    public int RestartCount { get; set; }
    public Task RestartLoopAsync() {
        RestartCount++;
        return Task.CompletedTask;
    }

    public int StartDaemonCallCount { get; set; }
    public Func<CancellationToken, Task<StartDaemonResult>>? StartBehavior { get; set; }
    public Task<StartDaemonResult> StartDaemonAsync(CancellationToken ct) {
        StartDaemonCallCount++;
        return (StartBehavior ?? (_ => Task.FromResult(new StartDaemonResult(true, null))))(ct);
    }

    public static DaemonStatusDto Snap(
            string daemon = "daemon-a", string version = "1.2.3", string serverUrl = "http://localhost:9999",
            string connection = "connected", int active = 0, int max = 5, int? pid = null, string? instanceId = null,
            string[]? supportedVendors = null, Dictionary<string, VendorModelOption[]>? vendorModels = null,
            Dictionary<string, VendorModelOption[]>? vendorModelsByAccount = null,
            string? credential = null, string? profile = null) {
        var agents = Enumerable.Range(0, active).Select(i => new AgentStatusDto(
            Id: $"a{i}", Kind: "agent", Vendor: "claude", RepoPath: null, Status: "Running",
            FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
            RequesterDisplay: null
        )).ToList();
        return new DaemonStatusDto(
            new DaemonInfoDto(daemon, version, serverUrl, connection, max, active, pid, instanceId, supportedVendors, vendorModels,
                credential, profile, vendorModelsByAccount),
            agents);
    }
}
