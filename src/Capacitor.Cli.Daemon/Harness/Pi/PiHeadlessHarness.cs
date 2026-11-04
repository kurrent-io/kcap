using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Headless;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>Pi headless: every tool, extension and user instruction source off, in a private directory per run. Pi has
/// no reasoning effort, turn or spend cap, so those request fields are ignored and the deadline is the run's only bound.</summary>
sealed partial class PiHeadlessHarness(
        HarnessRegistry harnesses, DaemonConfig config, HeadlessRunRoot runs, TimeProvider time, ILogger<PiHeadlessHarness> logger
    ) : IHeadlessHarness {
    public string Harness => "pi";

    public bool IsAvailable() => Executable() is not null;

    string? Executable() => config.Binaries.Resolve(harnesses.LaunchCommand(HarnessId.Pi, config.PiPath));

    public Task<ClaudeCliOutcome> RunAsync(RunHeadlessPromptArgs args, TimeSpan timeout, CancellationToken ct) =>
        PiCliRunner.RunDetailedAsync(args.Prompt, timeout, time, Log, runs,
            model: args.Model, jsonSchema: args.JsonSchema, systemPrompt: args.SystemPrompt,
            executable: Executable() ?? "", ct: ct);

    void Log(string message) => LogRunner(logger, message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "pi headless: {Message}")]
    static partial void LogRunner(ILogger logger, string message);
}
