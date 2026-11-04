using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Headless;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.Pi;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>What the server sends for Pi reaches the command line and stdin: the model and the system prompt are passed
/// through, and the harness is offered only when the resolved binary exists.</summary>
public class PiHeadlessHarnessTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Bin  { get; init; }

    PiHeadlessHarness Harness(BinaryProbe binaries) {
        var config = new DaemonConfig {
            Binaries = binaries,
            Profiles = new ProfileContext(new(null, "default", null, null), new ProfileConfig()),
        };

        return new(TestHarnesses.Under(Home, binaries), config, new HeadlessRunRoot(Bin.PathTo("state"), "test"),
            TimeProvider.System, NullLogger<PiHeadlessHarness>.Instance);
    }

    [Test]
    public async Task It_is_pi_and_available_only_when_the_binary_resolves() {
        var present = Harness(TestBinaries.Searching(Bin, "pi"));
        var absent  = Harness(TestBinaries.None);

        await Assert.That(present.Harness).IsEqualTo("pi");
        await Assert.That(present.IsAvailable()).IsTrue();
        await Assert.That(absent.IsAvailable()).IsFalse();
    }

    [Test]
    public async Task The_model_reaches_the_command_line_the_system_prompt_its_file_and_the_prompt_stdin() {
        Skip.When(OperatingSystem.IsWindows(), "the fake pi is a POSIX shell script");

        var argsFile   = Bin.PathTo("args.txt");
        var stdinFile  = Bin.PathTo("stdin.txt");
        var systemCopy = Bin.PathTo("system.txt");
        var harness   = Harness(TestBinaries.Searching(Bin, "pi"));

        Bin.CreateExecutable("pi", $$"""
            #!/bin/sh
            printf '%s\n' "$@" > '{{argsFile}}'
            cat > '{{stdinFile}}'
            while [ $# -gt 0 ]; do
              if [ "$1" = "--system-prompt" ]; then cp "$2" '{{systemCopy}}'; fi
              shift
            done
            exit 0
            """);

        await harness.RunAsync(
            new RunHeadlessPromptArgs("r1", "pi", "some-model", "high", "Be terse.", "the-prompt", null, 1, null, DateTimeOffset.UtcNow.AddMinutes(1)),
            TimeSpan.FromSeconds(30), CancellationToken.None);

        var args = await File.ReadAllTextAsync(argsFile);

        await Assert.That(args).Contains("--model\nsome-model\n");
        await Assert.That(await File.ReadAllTextAsync(systemCopy)).IsEqualTo("Be terse.");
        await Assert.That(await File.ReadAllTextAsync(stdinFile)).Contains("the-prompt");
    }
}
