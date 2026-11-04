using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Headless;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.OpenCode;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.OpenCode;

/// <summary>What the server sends for OpenCode reaches the command line and the prompt file, the daemon's environment
/// is read for the run, and the harness is offered only when the resolved binary exists.</summary>
public class OpenCodeHeadlessHarnessTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Bin  { get; init; }

    OpenCodeHeadlessHarness Harness(BinaryProbe binaries, Func<IReadOnlyDictionary<string, string?>>? environment = null) {
        var config = new DaemonConfig {
            Binaries = binaries,
            Profiles = new ProfileContext(new(null, "default", null, null), new ProfileConfig()),
        };

        return new(TestHarnesses.Under(Home, binaries), config, new HeadlessRunRoot(Bin.PathTo("state"), "test"),
            TimeProvider.System, environment ?? (() => new Dictionary<string, string?>()), NullLogger<OpenCodeHeadlessHarness>.Instance);
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
}
