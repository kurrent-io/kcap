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
            var system = string.IsNullOrWhiteSpace(systemPrompt) ? NeutralSystemPrompt : systemPrompt;

            return await RunCoreAsync(BuildPrompt(prompt, schema), timeout, time, log, run.Path, model, schema, system, executable, ct);
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
            string            systemPrompt,
            string            executable,
            CancellationToken ct
        ) {
        var psi = new ProcessStartInfo {
            FileName               = executable,
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            StandardInputEncoding  = Utf8NoBom,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            Environment = {
                [HostedAgent.SkipVar] = "1"
            }
        };

        foreach (var arg in BuildArgs(systemPrompt, model)) {
            psi.ArgumentList.Add(arg);
        }

        using var process = CliProcess.TryStart(psi, log);

        if (process is null) {
            log("Failed to start pi process");

            return new(null, ClaudeCliFailure.ProcessFailure, Error: "pi could not be started");
        }

        // Read without a token so the events seen before a kill survive it.
        var events     = new PiJsonEvents();
        var stdoutTask = ReadEventsAsync(process.StandardOutput, events);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(timeout, time);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try {
            await process.StandardInput.WriteAsync(input.AsMemory(), linkedCts.Token);
            await process.StandardInput.FlushAsync(linkedCts.Token);
            process.StandardInput.Close();

            await process.WaitForExitAsync(linkedCts.Token);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            await KillAsync(process, stdoutTask, time);

            throw;
        } catch (OperationCanceledException) {
            log($"Pi process timed out ({timeout.TotalSeconds:0}s), killing");
            await KillAsync(process, stdoutTask, time);

            return new(null, ClaudeCliFailure.Timeout, events.Usage);
        } catch (Exception ex) {
            log($"Failed to stream prompt to pi stdin: {ex.Message}");
            await KillAsync(process, stdoutTask, time);

            return new(null, ClaudeCliFailure.ProcessFailure, events.Usage, Error: ex.Message);
        }

        try {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(linkedCts.Token);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (OperationCanceledException) {
            // A descendant that outlived pi holds the pipe; pi itself has written all it will.
            log("Pi exited but its output stayed open to the deadline; using what was read");
            CloseOutput(process);
        }

        return Outcome(events, process.ExitCode, stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "", model, schema, log);
    }

    static ClaudeCliOutcome Outcome(PiJsonEvents events, int exitCode, string stderr, string? model, string? schema, Action<string> log) {
        var usage = events.Usage;

        switch (events.StopReason) {
            case null: {
                var trimmed = stderr.Trim();
                var tail    = trimmed.Length > 800 ? "…" + trimmed[^800..] : trimmed;
                var message = tail.Length > 0 ? tail : $"pi exited with code {exitCode} and no reply";
                log($"Pi ended without a reply: {SanitizeForLog(message)}");

                return new(null, HeadlessFailureClassifier.Classify(null, tail), usage, Error: message);
            }
            case "error": {
                var message = events.ErrorMessage;
                log($"Pi reported an error: {SanitizeForLog(message ?? "")}");

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
                Model: string.IsNullOrWhiteSpace(model) ? null : model,
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
    /// The ordered <c>pi</c> argument list. The prompt goes on stdin, never on the command line. Both system
    /// prompt flags are always passed: they are the only way to displace the user's <c>SYSTEM.md</c> and
    /// <c>APPEND_SYSTEM.md</c>, which <c>--no-context-files</c> leaves in place.
    /// </summary>
    internal static List<string> BuildArgs(string systemPrompt, string? model) {
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
            "--system-prompt", systemPrompt,
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

    static async Task ReadEventsAsync(StreamReader stdout, PiJsonEvents events) {
        try {
            while (await stdout.ReadLineAsync() is { } line) {
                events.Accept(line);
            }
        } catch (Exception e) when (e is ObjectDisposedException or IOException) { }
    }

    /// <summary>The run directory is removed next, so the process is waited out; a grandchild that inherited
    /// stdout can hold the pipe open past the kill, so the waits are bounded.</summary>
    static async Task KillAsync(Process process, Task stdoutTask, TimeProvider time) {
        try { ProcessTree.Kill(process); } catch {
            /* already gone */
        }

        try {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5), time);
            await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5), time);
        } catch {
            CloseOutput(process);
        }
    }

    /// <summary>Ends reads a descendant would otherwise keep pending: the process does not dispose its own readers.</summary>
    static void CloseOutput(Process process) {
        try { process.StandardOutput.Dispose(); } catch { /* best effort */ }
        try { process.StandardError.Dispose(); } catch { /* best effort */ }
    }

    static string SanitizeForLog(string value) {
        var sb = new StringBuilder(value.Length);

        foreach (var ch in value) {
            if (ch is '\r' or '\n') {
                sb.Append("\\n");
                continue;
            }

            if (char.IsControl(ch)) continue;

            sb.Append(ch);
        }

        return sb.ToString();
    }
}
