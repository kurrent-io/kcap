using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="ServerConnection.AdvertiseCapabilitiesAsync"/>: a change that concerns only the daemon reaches a server
/// that takes <c>DaemonUpdateCapabilities</c> on its own, leaving readiness and every agent registration as they were,
/// and reaches any other server as the full re-registration.
/// </summary>
public class ServerConnectionCapabilityUpdateTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    sealed class UpdatingConnection(DaemonConfig config) : ServerConnection(
        config, new FixedCredentialSource(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System) {
        public List<DaemonConnect>            Connects { get; } = [];
        public List<DaemonCapabilitiesUpdate> Updates  { get; } = [];
        public List<bool>                     ReadyWhileUpdating { get; } = [];

        public int AgentReRegistrations { get; private set; }

        public Func<DaemonCapabilitiesUpdate, Task<DaemonCapabilitiesUpdateResponse?>> Answer { get; set; } =
            _ => Task.FromResult<DaemonCapabilitiesUpdateResponse?>(new(true));

        public TaskCompletionSource? ConnectGate { get; set; }
        public TaskCompletionSource  ConnectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ConnectionId { get; set; } = "connection-1";

        internal override HubConnectionState HubState => HubConnectionState.Connected;

        internal override string CurrentConnectionId => ConnectionId;

        public UpdatingConnection Wired() {
            ReRegisterAgentsHook = () => {
                AgentReRegistrations++;

                return Task.CompletedTask;
            };

            return this;
        }

        internal override async Task InvokeDaemonConnectRawAsync(DaemonConnect connect) {
            Connects.Add(connect);
            ConnectEntered.TrySetResult();
            if (ConnectGate is { } gate) await gate.Task;
        }

        internal override Task<DaemonCapabilitiesUpdateResponse?> InvokeUpdateCapabilitiesRawAsync(DaemonCapabilitiesUpdate update) {
            Updates.Add(update);
            ReadyWhileUpdating.Add(IsReady);

            return Answer(update);
        }
    }

    DaemonConfig NewConfig() => new() { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };

    static HubException MissingMethod() =>
        new($"Failed to invoke '{ServerConnection.UpdateCapabilitiesMethod}' due to an error on the server. HubException: Method does not exist.");

    static async Task<UpdatingConnection> RegisteredAsync(DaemonConfig config) {
        var conn = new UpdatingConnection(config).Wired();
        await conn.ReRegisterAsync();

        return conn;
    }

    [Test]
    public async Task A_capability_change_goes_out_on_its_own_and_re_registers_no_agent() {
        var config = NewConfig();
        await using var conn = await RegisteredAsync(config);
        var registered = 0;
        conn.Registered += () => registered++;
        config.SupportedVendors = ["claude", "codex"];

        await conn.AdvertiseCapabilitiesAsync();

        await Assert.That(conn.Updates.Single().SupportedVendors).IsEquivalentTo(["claude", "codex"]);
        await Assert.That(conn.Connects.Count).IsEqualTo(1).Because("only the registration before the change");
        await Assert.That(conn.AgentReRegistrations).IsEqualTo(1).Because("only the registration before the change");
        await Assert.That(conn.ReadyWhileUpdating.Single()).IsTrue();
        await Assert.That(conn.IsReady).IsTrue();
        await Assert.That(registered).IsEqualTo(0);
    }

    [Test]
    public async Task An_applied_update_acknowledges_the_codex_resolution_it_carried() {
        var config = NewConfig();
        await using var conn = await RegisteredAsync(config);
        var codex = CodexTransportResolution.Resolve(config.AcknowledgedCodexResolution.Generation + 1, switchOn: true, () => "0.146.0");
        config.Advertise(new List<UnattendedVendorCapability>(), codex);

        await conn.AdvertiseCapabilitiesAsync();

        await Assert.That(config.AcknowledgedCodexResolution).IsSameReferenceAs(codex);
    }

    [Test]
    public async Task A_server_without_the_method_gets_the_full_re_registration_and_is_asked_again_only_on_a_new_connection() {
        await using var conn = new UpdatingConnection(NewConfig()).Wired();
        conn.Answer = _ => Task.FromException<DaemonCapabilitiesUpdateResponse?>(MissingMethod());

        await conn.AdvertiseCapabilitiesAsync();
        await conn.AdvertiseCapabilitiesAsync();

        await Assert.That(conn.Updates.Count).IsEqualTo(1);
        await Assert.That(conn.Connects.Count).IsEqualTo(2);
        await Assert.That(conn.AgentReRegistrations).IsEqualTo(2);

        conn.ConnectionId = "connection-2";
        await conn.AdvertiseCapabilitiesAsync();

        await Assert.That(conn.Updates.Count).IsEqualTo(2).Because("a reconnect may have reached an upgraded server");
    }

    [Test]
    [Arguments(false)]
    [Arguments(null)]
    public async Task An_update_the_server_did_not_apply_falls_back_to_the_full_re_registration(bool? applied) {
        await using var conn = new UpdatingConnection(NewConfig()).Wired();
        conn.Answer = _ => Task.FromResult(applied is { } value ? new DaemonCapabilitiesUpdateResponse(value) : null);

        await conn.AdvertiseCapabilitiesAsync();

        await Assert.That(conn.Updates.Count).IsEqualTo(1);
        await Assert.That(conn.Connects.Count).IsEqualTo(1);
        await Assert.That(conn.AgentReRegistrations).IsEqualTo(1);
        await Assert.That(conn.IsReady).IsTrue();
    }

    /// <summary>A failure that says nothing about the method must not become a re-registration: one overlapping a slow
    /// server is what the update exists to avoid.</summary>
    [Test]
    public async Task A_failed_update_throws_without_re_registering_and_marks_its_codex_resolution_failed() {
        var config = NewConfig();
        var codex  = CodexTransportResolution.Resolve(1, switchOn: true, () => "0.146.0");
        config.Advertise(new List<UnattendedVendorCapability>(), codex);
        await using var conn = new UpdatingConnection(config).Wired();
        conn.Answer = _ => Task.FromException<DaemonCapabilitiesUpdateResponse?>(new HubException("handler failed"));

        await Assert.That(conn.AdvertiseCapabilitiesAsync).Throws<HubException>();

        await Assert.That(conn.Connects).IsEmpty();
        await Assert.That(config.FailedCodexGeneration).IsEqualTo(codex.Generation);
        await Assert.That(config.AcknowledgedCodexResolution).IsNotSameReferenceAs(codex);
    }

    /// <summary>The server runs one client's invocations in parallel, so an update sent while a connect's older
    /// snapshot is still on the wire could land first and be overwritten by it.</summary>
    [Test]
    public async Task An_update_waits_for_a_connect_already_sending() {
        var config = NewConfig();
        var gate   = new TaskCompletionSource();
        await using var conn = new UpdatingConnection(config).Wired();
        conn.ConnectGate = gate;

        var registering = conn.ReRegisterAsync();
        await conn.ConnectEntered.Task;
        config.SupportedVendors = ["pi"];
        var updating = conn.AdvertiseCapabilitiesAsync();
        // Waiting for a negative: the update must not go out while the connect is held open.
        await Task.Delay(100);
        await Assert.That(conn.Updates).IsEmpty();

        gate.SetResult();
        await registering;
        await updating;

        await Assert.That(conn.Updates.Single().SupportedVendors).IsEquivalentTo(["pi"]);
    }

    [Test]
    public async Task The_update_carries_what_a_connect_would_for_every_field_it_names() {
        var config = NewConfig();
        config.MaxConcurrentAgents   = 7;
        config.SupportedVendors      = ["claude", "codex"];
        config.UnattendedVendors     = ["claude"];
        config.AcpPresetVendors      = ["codex"];
        config.PermissionModeVendors = ["claude"];
        config.PrReviewVendors       = ["claude", "codex"];
        config.VendorModels          = new(StringComparer.Ordinal) { ["pi"] = [new("p/a", "A · p")] };
        config.VendorModelsByAccount = new(StringComparer.Ordinal) { ["acc-1"] = [new("p/b", "B · p")] };
        config.Advertise([new UnattendedVendorCapability("claude", "2.1.263", "1", false)], config.CodexResolution);
        await using var conn = new UpdatingConnection(config);
        conn.RunHeadlessPromptHandler = _ => Task.FromResult(default(HeadlessPromptResponse));
        conn.GetHeadlessVendors       = () => ["claude"];
        conn.OnCompactAgent          += _ => Task.CompletedTask;

        var connect = conn.BuildDaemonConnect("mac", [], [], null, config.Advertisement);
        var update  = conn.BuildCapabilitiesUpdate(config.Advertisement);

        await Assert.That(update.MaxAgents).IsEqualTo(connect.MaxAgents);
        await Assert.That(update.SupportedVendors).IsSameReferenceAs(connect.SupportedVendors);
        await Assert.That(update.UnattendedVendors).IsSameReferenceAs(connect.UnattendedVendors);
        await Assert.That(update.UnattendedVendorCapabilities).IsSameReferenceAs(connect.UnattendedVendorCapabilities);
        await Assert.That(update.AcpPresetVendors).IsSameReferenceAs(connect.AcpPresetVendors);
        await Assert.That(update.PermissionModeVendors).IsSameReferenceAs(connect.PermissionModeVendors);
        await Assert.That(update.PrReviewVendors).IsSameReferenceAs(connect.PrReviewVendors);
        await Assert.That(update.VendorModels).IsSameReferenceAs(connect.VendorModels);
        await Assert.That(update.VendorModelsByAccount).IsSameReferenceAs(connect.VendorModelsByAccount);
        await Assert.That(update.HeadlessVendors).IsEquivalentTo(connect.HeadlessVendors!);
        await Assert.That(update.Capabilities).IsEquivalentTo(connect.Capabilities!);
    }

    /// <summary>The server binds these names; they are the contract its handler is written against.</summary>
    [Test]
    public async Task The_update_and_its_answer_go_over_the_hub_with_snake_case_names() {
        var options = new JsonSerializerOptions();
        ServerConnection.ConfigureHubPayload(options);
        var update = new DaemonCapabilitiesUpdate(
            2, ["claude"], ["claude"], [], ["codex"], ["claude"], ["claude"],
            new(StringComparer.Ordinal) { ["pi"] = [new("p/a", "A")] }, null, [], null);

        var json   = JsonSerializer.Serialize(update, options);
        var answer = JsonSerializer.Deserialize<DaemonCapabilitiesUpdateResponse>("""{"applied":true}""", options);

        await Assert.That(json).IsEqualTo(
            """{"max_agents":2,"supported_vendors":["claude"],"unattended_vendors":["claude"],"unattended_vendor_capabilities":[],"acp_preset_vendors":["codex"],"permission_mode_vendors":["claude"],"pr_review_vendors":["claude"],"vendor_models":{"pi":[{"value":"p/a","label":"A"}]},"vendor_models_by_account":null,"headless_vendors":[],"capabilities":null}""");
        await Assert.That(answer!.Applied).IsTrue();
    }
}
