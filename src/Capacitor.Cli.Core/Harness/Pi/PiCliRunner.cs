using System.Diagnostics;
using System.Text;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Headless;

namespace Capacitor.Cli.Core.Harness.Pi;

/// <summary>
/// Headless invocation of <c>pi -p --mode json</c>, returning the same records as <see cref="ClaudeCliRunner"/>.
/// </summary>
/// <remarks>
/// Pi exits 0 when the provider refuses the request, so the outcome comes from the final assistant message,
/// never the exit code. Pi has no turn or spend cap: the timeout is the only bound on the run.
/// </remarks>
static class PiCliRunner {
    public const string NeutralSystemPrompt = "Answer the request directly.";

    const string SystemPromptFile = "system-prompt.md";

    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Runs the prompt once with every tool, extension and user instruction source off, inside a fresh
    /// private directory under <paramref name="runs"/>, removed afterwards. With <paramref name="jsonSchema"/>,
    /// the result is the reply re-serialised once it matches the schema. External cancellation kills the
    /// process and propagates; the timeout kills it and returns <see cref="ClaudeCliFailure.Timeout"/>.
    /// </summary>
    public static async Task<ClaudeCliOutcome> RunDetailedAsync(
            string            prompt,
            TimeSpan          timeout,
            TimeProvider      time,
            Action<string>    log,
            HeadlessRunRoot   runs,
            string?           model,
            string?           jsonSchema,
            string?           systemPrompt,
            string            executable,
            CancellationToken ct
        ) {
        var schema = string.IsNullOrWhiteSpace(jsonSchema) ? null : jsonSchema;

        if (schema is not null && StructuredReply.Refusal(schema) is { } refusal) {
            log(refusal);

            return new(null, ClaudeCliFailure.ProcessFailure, Error: refusal);
        }

        HeadlessRunDirectory run;

        try {
            run = runs.Create("pi");
        } catch (HeadlessRunRefusedException ex) {
            log(ex.Message);

            return new(null, ClaudeCliFailure.ProcessFailure, Error: ex.Message);
        }

        using (run) {
            HeadlessRunDirectory cwd;

            try {
                cwd = run.Child("cwd");
            } catch (HeadlessRunRefusedException ex) {
                log(ex.Message);

                return new(null, ClaudeCliFailure.ProcessFailure, Error: ex.Message);
            }

            using (cwd) {
                // A file, never argv: on Windows pi is a cmd.exe shim, which cuts an argument at its first
                // newline and expands %VAR% in it.
                var systemFile = Path.Combine(run.Path, SystemPromptFile);
                var system     = string.IsNullOrWhiteSpace(systemPrompt) ? NeutralSystemPrompt : systemPrompt;
                await File.WriteAllTextAsync(systemFile, system, Utf8NoBom, ct);

                return await RunCoreAsync(BuildPrompt(prompt, schema), timeout, time, log, cwd.Path, model, schema, systemFile, executable, ct);
            }
        }
    }

    static async Task<ClaudeCliOutcome> RunCoreAsync(
            string            input,
            TimeSpan          timeout,
            TimeProvider      time,
            Action<string>    log,
            string            workingDir,
            string?           model,
            string?           schema,
            string            systemPromptFile,
            string            executable,
            CancellationToken ct
        ) {
        var psi = new ProcessStartInfo {
            FileName         = executable,
            WorkingDirectory = workingDir,
            Environment = {
                [HostedAgent.SkipVar] = "1"
            }
        };

        foreach (var arg in BuildArgs(systemPromptFile, model)) {
            psi.ArgumentList.Add(arg);
        }

        var events = new PiJsonEvents();
        var exit   = await HeadlessProcess.RunAsync(psi, input, events.Accept, null, timeout, time, log, ct);

        if (exit is null) return new(null, ClaudeCliFailure.ProcessFailure, Error: "pi could not be started");
        if (exit.TimedOut) return new(null, ClaudeCliFailure.Timeout, events.Usage);

        return Outcome(events, exit, model, schema, log);
    }

    static ClaudeCliOutcome Outcome(PiJsonEvents events, HeadlessProcessExit exit, string? model, string? schema, Action<string> log) {
        var usage = events.Usage;

        switch (events.StopReason) {
            case null: {
                var trimmed = exit.Stderr.Trim();
                var tail    = trimmed.Length > 800 ? "…" + trimmed[^800..] : trimmed;
                var message = tail.Length > 0 ? tail : $"pi exited with code {exit.ExitCode} and no reply";
                log($"Pi ended without a reply: {HeadlessProcess.ForLog(message)}");

                return new(null, HeadlessFailureClassifier.Classify(null, tail), usage, Error: message);
            }
            case "error": {
                var message = events.ErrorMessage;
                log($"Pi reported an error: {HeadlessProcess.ForLog(message ?? "")}");

                return new(null, HeadlessFailureClassifier.Classify(HeadlessFailureClassifier.StatusFromMessage(message), message), usage,
                    Error: message ?? "pi stopped with an error");
            }
            case not "stop":
                log($"Pi stopped with '{events.StopReason}'");

                return new(null, ClaudeCliFailure.ProcessFailure, usage, Error: $"pi stopped with '{events.StopReason}'");
        }

        var reply = events.Reply?.Trim();

        if (string.IsNullOrEmpty(reply)) {
            log("Pi produced an empty reply");

            return new(null, ClaudeCliFailure.OutputUnparseable, usage);
        }

        if (schema is not null) {
            if (StructuredReply.Accept(reply, schema) is not { } structured) {
                log("Pi reply rejected: it does not match the JSON schema");

                return new(null, ClaudeCliFailure.OutputUnparseable, usage);
            }

            reply = structured;
        }

        return new(
            new ClaudeCliResult(
                Result: reply,
                Model: string.IsNullOrWhiteSpace(model) ? events.Model : model,
                InputTokens: usage?.InputTokens           ?? 0,
                OutputTokens: usage?.OutputTokens         ?? 0,
                CacheReadTokens: usage?.CacheReadTokens   ?? 0,
                CacheWriteTokens: usage?.CacheWriteTokens ?? 0,
                CostUsd: usage?.CostUsd,
                NumTurns: 1
            ),
            null,
            usage
        );
    }

    /// <summary>
    /// The ordered <c>pi</c> argument list. The prompt goes on stdin and the system prompt in a file, which
    /// <c>--system-prompt</c> reads when given an existing path. Both system prompt flags are always passed:
    /// they are the only way to displace the user's <c>SYSTEM.md</c> and <c>APPEND_SYSTEM.md</c>, which
    /// <c>--no-context-files</c> leaves in place.
    /// </summary>
    internal static List<string> BuildArgs(string systemPromptFile, string? model) {
        List<string> args = [
            "-p",
            "--mode", "json",
            "--no-approve",
            "--no-tools",
            "--no-extensions",
            "--no-skills",
            "--no-prompt-templates",
            "--no-themes",
            "--no-context-files",
            "--no-session",
            // Without it, Pi installs the user's configured npm packages at startup.
            "--offline",
            "--system-prompt", systemPromptFile,
            "--append-system-prompt", ""
        ];

        if (!string.IsNullOrWhiteSpace(model)) {
            args.Add("--model");
            args.Add(model);
        }

        return args;
    }

    internal static string BuildPrompt(string prompt, string? jsonSchema) =>
        string.IsNullOrWhiteSpace(jsonSchema) ? prompt : $"{prompt}\n\n{StructuredReply.SchemaInstruction(jsonSchema)}";
}
