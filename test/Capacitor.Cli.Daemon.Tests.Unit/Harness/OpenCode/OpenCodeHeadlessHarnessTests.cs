using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Headless;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Daemon.Tests.Unit.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.OpenCode;

/// <summary>What the server sends for OpenCode reaches the command line and the prompt file, the daemon's environment
/// is read for the run, and the harness is offered only when the resolved binary exists.</summary>
public class OpenCodeHeadlessHarnessTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Bin  { get; init; }

    OpenCodeHeadlessHarness Harness(BinaryProbe binaries, Func<IReadOnlyDictionary<string, string?>>? environment = null) =>
        Harness(binaries, TimeProvider.System, NullLogger<OpenCodeHeadlessHarness>.Instance, environment);

    OpenCodeHeadlessHarness Harness(BinaryProbe binaries, TimeProvider time, ILogger<OpenCodeHeadlessHarness> logger,
        Func<IReadOnlyDictionary<string, string?>>? environment = null) {
        var config = new DaemonConfig {
            Binaries = binaries,
            Profiles = new ProfileContext(new(null, "default", null, null), new ProfileConfig()),
        };

        return new(TestHarnesses.Under(Home, binaries), config, new HeadlessRunRoot(Bin.PathTo("state"), "test"),
            time, environment ?? (() => new Dictionary<string, string?>()), logger);
    }

    [Test]
    public async Task It_is_opencode_and_available_only_when_the_binary_resolves() {
        var present = Harness(TestBinaries.Searching(Bin, "opencode"));
        var absent  = Harness(TestBinaries.None);

        await Assert.That(present.Harness).IsEqualTo("opencode");
        await Assert.That(present.IsAvailable()).IsTrue();
        await Assert.That(absent.IsAvailable()).IsFalse();
    }

    [Test]
    public async Task The_request_reaches_the_command_line_and_prompt_file_and_the_injected_environment_reaches_the_child() {
        Skip.When(OperatingSystem.IsWindows(), "the fake opencode is a POSIX shell script");

        var argsFile   = Bin.PathTo("args.txt");
        var envFile    = Bin.PathTo("env.txt");
        var promptCopy = Bin.PathTo("prompt-copy.txt");
        var harness = Harness(TestBinaries.Searching(Bin, "opencode"),
            () => new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin", ["KCAP_PROBE"] = "from-daemon" });

        Bin.CreateExecutable("opencode", $$"""
            #!/bin/sh
            printf '%s\n' "$@" > '{{argsFile}}'
            printf '%s' "$KCAP_PROBE" > '{{envFile}}'
            while [ $# -gt 0 ]; do
              if [ "$1" = "--file" ]; then cp "$2" '{{promptCopy}}'; fi
              shift
            done
            exit 0
            """);

        await harness.RunAsync(
            new RunHeadlessPromptArgs("r1", "opencode", "prov/some-model", "high", "Be terse.", "the-prompt", null, 1, null, DateTimeOffset.UtcNow.AddMinutes(1)),
            TimeSpan.FromSeconds(30), CancellationToken.None);

        var prompt = await File.ReadAllTextAsync(promptCopy);

        await Assert.That(await File.ReadAllTextAsync(argsFile)).Contains("-m\nprov/some-model\n");
        await Assert.That(await File.ReadAllTextAsync(envFile)).IsEqualTo("from-daemon");
        await Assert.That(prompt).Contains("the-prompt");
        await Assert.That(prompt).Contains("Be terse.");
    }

    /// <summary>The runner's own timeout, which keeps the usage reported so far, must fire before the daemon's outer
    /// deadline: the fake clock reaches the runner's timeout only if it is the margin short of the deadline, never the
    /// longer timeout the daemon offered, and the log names the length it armed.</summary>
    [Test]
    [Arguments(10, 8)]
    [Arguments(1, 0)]
    public async Task The_runner_times_out_the_margin_before_the_deadline_and_never_below_zero(int deadlineSeconds, int runnerSeconds) {
        Skip.When(OperatingSystem.IsWindows(), "the fake opencode is a POSIX shell script");

        var started = Bin.PathTo("started");
        var clock   = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var logger  = new CapturingLogger<OpenCodeHeadlessHarness>();
        var harness = Harness(TestBinaries.Searching(Bin, "opencode"), clock, logger, () => new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin" });
        Bin.CreateExecutable("opencode", $"#!/bin/sh\ntouch '{started}'\nexec sleep 30\n");

        var run = harness.RunAsync(
            new RunHeadlessPromptArgs("r1", "opencode", "", null, null, "the-prompt", null, 1, null, clock.GetUtcNow().AddSeconds(deadlineSeconds)),
            TimeSpan.FromSeconds(60), CancellationToken.None);

        if (runnerSeconds > 0) {
            var waited = Environment.TickCount64;
            while (!File.Exists(started) && Environment.TickCount64 - waited < 10_000) await Task.Delay(20);
            // Again until the run ends: the process starts before the runner arms its timer, so one advance can land first.
            while (!run.IsCompleted && Environment.TickCount64 - waited < 20_000) {
                clock.Advance(TimeSpan.FromSeconds(runnerSeconds));
                await Task.Delay(50);
            }
        }

        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.Timeout);
        await Assert.That(logger.Entries.Select(e => e.Message)).Contains(m => m.Contains($"timed out ({runnerSeconds}s)"));
    }
}
