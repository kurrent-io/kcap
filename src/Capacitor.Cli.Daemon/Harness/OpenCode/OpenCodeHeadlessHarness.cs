using System.Collections;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Headless;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.OpenCode;

/// <summary>OpenCode headless: its config, data and state redirected into a private directory per run, with every tool
/// denied. OpenCode has no reasoning effort, turn or spend cap to set, so those request fields are ignored.
/// <paramref name="environment"/> is read per run: the daemon's environment in production.</summary>
sealed partial class OpenCodeHeadlessHarness(
        HarnessRegistry harnesses, DaemonConfig config, HeadlessRunRoot runs, TimeProvider time,
        Func<IReadOnlyDictionary<string, string?>> environment, ILogger<OpenCodeHeadlessHarness> logger
    ) : IHeadlessHarness {
    public string Harness => "opencode";

    public bool IsAvailable() => Executable() is not null;

    /// <summary>The daemon process's environment, as the production <c>environment</c> argument.</summary>
    public static IReadOnlyDictionary<string, string?> ProcessEnvironment() {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) {
            env[(string)entry.Key] = entry.Value as string;
        }

        return env;
    }

    string? Executable() => config.Binaries.Resolve(harnesses.LaunchCommand(HarnessId.OpenCode, config.OpenCodePath));

    public Task<ClaudeCliOutcome> RunAsync(RunHeadlessPromptArgs args, TimeSpan timeout, CancellationToken ct) =>
        OpenCodeCliRunner.RunDetailedAsync(args.Prompt, HeadlessDeadlineMargin.RunnerTimeout(args.Deadline, timeout, time), time, Log, runs,
            harnesses.Of<OpenCodeHarness>().Paths, environment(),
            model: args.Model, jsonSchema: args.JsonSchema, systemPrompt: args.SystemPrompt,
            executable: Executable() ?? "", ct: ct);

    void Log(string message) => LogRunner(logger, message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "opencode headless: {Message}")]
    static partial void LogRunner(ILogger logger, string message);
}
