using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.OpenCode;

/// <summary>
/// A real <c>opencode acp</c> reviewer through the real factory, with an MCP server and instructions planted in every
/// global config location. Spends a real model call, so it runs only with <see cref="LiveGateEnvVar"/>=1,
/// <c>opencode</c> on PATH and <c>ANTHROPIC_API_KEY</c> set. The planted config also carries the model, so the reply
/// proves the user's default survives.
/// </summary>
[NotInParallel]
public class OpenCodeReviewerLiveTests {
    const string LiveGateEnvVar = "KCAP_OPENCODE_LIVE";
    const string Model          = "anthropic/claude-haiku-4-5";

    static readonly TimeSpan Turn = TimeSpan.FromSeconds(120);

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempDir]         public required TempDir         Tmp     { get; init; }

    sealed class RecordingConnection() : ServerConnection(
            new() { Name = "test", ServerUrl = "http://127.0.0.1:1" }, new FixedCredentialSource(),
            NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System) {
        public bool AskedForInteraction { get; private set; }

        public override Task<AcpInteractionDecision> RequestAcpInteractionAsync(AcpInteractionRequest request, CancellationToken ct = default) {
            AskedForInteraction = true;

            return Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null));
        }
    }

    /// <summary>The control first: the same two files with OpenCode's own defaults resolve both planted servers, so a
    /// location OpenCode never read cannot pass. Then the reviewer starts, reports through its result channel with the
    /// user's home, and neither planted server has started.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_planted_global_server_never_starts_and_the_result_channel_still_reports() {
        Skip.Unless(Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Spends a real model call: set {LiveGateEnvVar}=1 with `opencode` on PATH and ANTHROPIC_API_KEY set to run");
        Skip.When(OperatingSystem.IsWindows(), "The OpenCode unattended reviewer is POSIX-only");
        Skip.When(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")), "ANTHROPIC_API_KEY is not set");

        var opencode = BinaryProbe.FromEnvironment().Resolve("opencode");
        Skip.When(opencode is null, "opencode is not on PATH");

        var home       = Tmp.CreateDir("user-home").Path;
        var configHome = Tmp.CreateDir("user-config").Path;
        var data       = Tmp.CreateDir("user-data").Path;

        Tmp.CreateFile(Path.Combine("user-config", "opencode", "opencode.json"), PlantedConfig("global", out var globalStarted));
        Tmp.CreateFile(Path.Combine("user-home", ".opencode", "opencode.json"), PlantedConfig("dot-opencode", out var dotStarted));

        using var homeScope   = EnvScope.Exclusive("HOME", home);
        using var testHome    = EnvScope.Exclusive("OPENCODE_TEST_HOME", null);
        using var configScope = EnvScope.Exclusive("XDG_CONFIG_HOME", configHome);
        using var dataScope   = EnvScope.Exclusive("XDG_DATA_HOME", data);
        using var dirScope    = EnvScope.Exclusive("OPENCODE_CONFIG_DIR", null);

        var control = await RunAsync(opencode!, "debug", "config");
        await Assert.That(control).Contains("planted-global");
        await Assert.That(control).Contains("planted-dot-opencode");

        var result  = Path.Combine(Tmp.Path, "result-called");
        var seen    = Path.Combine(Tmp.Path, "result-home");
        var channel = Tmp.CreateFile("flow-result.py", AcpHostedAgentRuntimeFactoryLiveTests.FakeFlowResultMcpScript);
        var kcap    = Tmp.CreateExecutable("fake-kcap", $"#!/bin/sh\nprintf '%s' \"$HOME\" > '{seen}'\nexec python3 '{channel}'\n");

        var config = new DaemonConfig {
            OpenCodeUnattendedReviewerEnabled = true,
            Store = Daemons.Store, Name = "live", DaemonEpoch = "live", Home = new UserHome(home), Binaries = BinaryProbe.FromEnvironment(),
        };
        AcpHostedAgentRuntimeFactory.VersionStoreFor(config, AcpVendorDescriptors.OpenCode.Vendor)
            .Affirm((await RunAsync(opencode!, "--version")).Trim());

        var worktree   = Tmp.CreateDir("worktree").Path;
        var connection = new RecordingConnection();
        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.OpenCode, config: config, loggerFactory: NullLoggerFactory.Instance,
            connection: connection, connectionSource: null, timeProvider: TimeProvider.System);

        var ctx = new RuntimeStartContext(
            AgentId: result, Vendor: "opencode", SourceRepoPath: worktree,
            Worktree: new WorktreeInfo(Path: worktree, Branch: "b", SourceRepo: worktree),
            Prompt: "Call submit_review_result exactly once with verdict CLEAN and summary ok. Do nothing else.",
            Model: "", Effort: null, Tools: null, IsReview: false, IsReviewFlow: true, Review: null, Cols: 80, Rows: 24,
            ServerUrl: "http://kcap.test", DaemonBridgeUrl: null, CapacitorPath: kcap, Work: WorkLocation.OwnedWorktree);

        using var cts     = new CancellationTokenSource(Turn);
        var       started = await factory.StartAsync(ctx, cts.Token);
        var       runtime = (AcpHostedAgentRuntime)started.Runtime;

        try {
            while (!File.Exists(result) && !runtime.HasExited && !cts.IsCancellationRequested) await Task.Delay(100);

            await Assert.That(File.Exists(result)).IsTrue();
            await Assert.That(connection.AskedForInteraction).IsFalse();
            await Assert.That(File.ReadAllText(seen)).IsEqualTo(home);
            await Assert.That(File.Exists(globalStarted)).IsFalse();
            await Assert.That(File.Exists(dotStarted)).IsFalse();
        } finally {
            await cts.CancelAsync();
            await runtime.DisposeAsync();
        }
    }

    string PlantedConfig(string name, out string marker) {
        marker = Path.Combine(Tmp.Path, $"started-{name}");
        var server = Tmp.CreateExecutable($"server-{name}.sh", $"#!/bin/sh\ntouch '{marker}'\nexec sleep 30\n");

        return new JsonObject {
            ["model"] = Model,
            ["mcp"] = new JsonObject {
                [$"planted-{name}"] = new JsonObject { ["type"] = "local", ["command"] = new JsonArray((JsonNode?)server), ["enabled"] = true },
            },
        }.ToJsonString();
    }

    static async Task<string> RunAsync(string executable, params string[] args) {
        var psi = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var       output  = process.StandardOutput.ReadToEndAsync();
        var       error   = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await error;

        return await output;
    }
}
