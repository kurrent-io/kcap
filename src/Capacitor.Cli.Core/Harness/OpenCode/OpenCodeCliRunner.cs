using System.Diagnostics;
using System.Text;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Headless;

namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>
/// Headless invocation of <c>opencode run --format json</c>, returning the same records as <see cref="ClaudeCliRunner"/>.
/// </summary>
/// <remarks>
/// The run gets private, empty config homes, data store and state, so none of the user's MCP servers, plugins,
/// agents or instructions load and the session never reaches the user's <c>opencode.db</c>. OpenCode has no
/// turn or spend cap: the timeout, and a spend limit logged on stderr, are the only bounds on the run.
/// </remarks>
static class OpenCodeCliRunner {
    /// <summary>The only text on the command line: the prompt itself would hit Windows' argv cap.</summary>
    public const string Message = "Answer the request in the attached file.";

    const string PromptFile = "prompt.md";

    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Runs the prompt once inside a fresh private directory under <paramref name="runs"/>, removed afterwards.
    /// <paramref name="inheritedEnv"/> is the environment OpenCode would otherwise get; the user's model and
    /// providers are read from the config it names. With <paramref name="jsonSchema"/>, the result is the reply
    /// re-serialised once it matches the schema. External cancellation kills the process and propagates; the
    /// timeout kills it and returns <see cref="ClaudeCliFailure.Timeout"/>.
    /// </summary>
    public static async Task<ClaudeCliOutcome> RunDetailedAsync(
            string                               prompt,
            TimeSpan                             timeout,
            TimeProvider                         time,
            Action<string>                       log,
            HeadlessRunRoot                      runs,
            OpenCodePaths                        paths,
            IReadOnlyDictionary<string, string?> inheritedEnv,
            string?                              model,
            string?                              jsonSchema,
            string?                              systemPrompt,
            string                               executable,
            CancellationToken                    ct
        ) {
        var schema = string.IsNullOrWhiteSpace(jsonSchema) ? null : jsonSchema;

        if (schema is not null && StructuredReply.Refusal(schema) is { } refusal) {
            log(refusal);

            return new(null, ClaudeCliFailure.ProcessFailure, Error: refusal);
        }

        if (OpenCodeHeadlessConfig.ManagedConfigFile(OpenCodeHeadlessConfig.ManagedConfigDir) is { } managed)
            log($"OpenCode's managed config '{managed}' applies to this run");

        HeadlessRunDirectory? run = null;

        try {
            var config = OpenCodeHeadlessConfig.Build(paths, inheritedEnv);
            run = runs.Create("opencode");

            using var cwd        = run.Child("cwd");
            using var home       = run.Child("home");
            using var configHome = run.Child("xdg-config");
            using var configDir  = run.Child("config");
            using var data       = run.Child("data");
            using var state      = run.Child("state");

            var promptFile = Path.Combine(cwd.Path, PromptFile);
            await File.WriteAllTextAsync(promptFile, BuildPrompt(prompt, systemPrompt, schema), Utf8NoBom, ct);

            var env = BuildEnvironment(inheritedEnv, new(home.Path, configHome.Path, configDir.Path, data.Path, state.Path), config, paths.Home);

            return await RunCoreAsync(promptFile, env, timeout, time, log, cwd.Path, model, schema, executable, ct);
        } catch (HeadlessRunRefusedException ex) {
            log(ex.Message);

            return new(null, ClaudeCliFailure.ProcessFailure, Error: ex.Message);
        } finally {
            run?.Dispose();
        }
    }

    static async Task<ClaudeCliOutcome> RunCoreAsync(
            string                      promptFile,
            Dictionary<string, string?> env,
            TimeSpan                    timeout,
            TimeProvider                time,
            Action<string>              log,
            string                      workingDir,
            string?                     model,
            string?                     schema,
            string                      executable,
            CancellationToken           ct
        ) {
        var psi = new ProcessStartInfo { FileName = executable, WorkingDirectory = workingDir };

        psi.Environment.Clear();
        foreach (var (key, value) in env) psi.Environment[key] = value;

        foreach (var arg in BuildArgs(promptFile, model)) psi.ArgumentList.Add(arg);

        var     events = new OpenCodeJsonEvents();
        string? limit  = null;

        var exit = await HeadlessProcess.RunAsync(psi, (string?)null, events.Accept, line => {
            if (limit is not null || OpenCodeLimitLog.Match(line) is not { } message) return false;

            limit = message;

            return true;
        }, timeout, time, log, ct);

        if (exit is null) return new(null, ClaudeCliFailure.ProcessFailure, Error: "opencode could not be started");

        if (exit.KilledEarly) {
            log($"OpenCode hit a spend limit: {HeadlessProcess.ForLog(limit ?? "")}");

            return new(null, ClaudeCliFailure.SpendBudget, events.Usage, Error: limit);
        }

        if (exit.TimedOut) return new(null, ClaudeCliFailure.Timeout, events.Usage);

        return Outcome(events, exit, model, schema, log);
    }

    static ClaudeCliOutcome Outcome(OpenCodeJsonEvents events, HeadlessProcessExit exit, string? model, string? schema, Action<string> log) {
        var usage = events.Usage;

        if (events.ErrorMessage is { } error) {
            log($"OpenCode reported an error: {HeadlessProcess.ForLog(error)}");

            var status = events.ErrorStatus ?? HeadlessFailureClassifier.StatusFromMessage(error);

            return new(null, HeadlessFailureClassifier.Classify(status, error), usage, Error: error);
        }

        if (exit.ExitCode != 0) {
            var trimmed = exit.Stderr.Trim();
            var tail    = trimmed.Length > 800 ? "…" + trimmed[^800..] : trimmed;
            var message = tail.Length > 0 ? tail : $"opencode exited with code {exit.ExitCode}";
            log($"OpenCode failed: {HeadlessProcess.ForLog(message)}");

            return new(null, HeadlessFailureClassifier.Classify(null, tail), usage, Error: message);
        }

        var reply = events.Reply?.Trim();

        if (string.IsNullOrEmpty(reply)) {
            log("OpenCode produced an empty reply");

            return new(null, ClaudeCliFailure.OutputUnparseable, usage);
        }

        // A stream cut off mid-step exits 0 with only part of the text.
        if (!events.StepFinished) {
            log("OpenCode exited before finishing the step its reply came from");

            return new(null, ClaudeCliFailure.ProcessFailure, usage, Error: "opencode exited before finishing its step");
        }

        if (schema is not null) {
            if (StructuredReply.Accept(reply, schema) is not { } structured) {
                log("OpenCode reply rejected: it does not match the JSON schema");

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

    /// <summary>The ordered <c>opencode</c> argument list. The prompt is attached as a file, never put on the
    /// command line. <c>--print-logs</c> is what surfaces a spend limit, which OpenCode otherwise retries
    /// silently.</summary>
    internal static List<string> BuildArgs(string promptFile, string? model) {
        List<string> args = [
            "run",
            "--format", "json",
            "--pure",
            "--print-logs",
            "--log-level", "ERROR",
            "--file", promptFile,
        ];

        if (!string.IsNullOrWhiteSpace(model)) {
            args.Add("-m");
            args.Add(model);
        }

        args.Add(Message);

        return args;
    }

    /// <summary>OpenCode has no flag replacing its system prompt, so the request's goes first in the prompt.</summary>
    internal static string BuildPrompt(string prompt, string? systemPrompt, string? jsonSchema) {
        var text = string.IsNullOrWhiteSpace(systemPrompt) ? prompt : $"<system_prompt>\n{systemPrompt.Trim()}\n</system_prompt>\n\n{prompt}";

        return string.IsNullOrWhiteSpace(jsonSchema) ? text : $"{text}\n\n{StructuredReply.SchemaInstruction(jsonSchema)}";
    }

    /// <summary>The inherited environment with the lockdown set over it. Provider keys are kept: for OpenCode a
    /// key usually is the sign-in. <c>OPENCODE_CONFIG_DIR</c> adds a config layer rather than replacing
    /// <c>$XDG_CONFIG_HOME/opencode</c>, so both are private, and OpenCode always loads
    /// <c>.opencode/opencode.json</c> under its home (<c>OPENCODE_TEST_HOME</c>, else the OS home), so both
    /// homes are private too. <c>OPENCODE_PERMISSION</c> is overwritten because it outranks every config's
    /// permissions, so an inherited allow-all would reopen the tools. The cache stays the user's, pinned
    /// because it would otherwise follow the private home: with models fetch off, it holds the model list.
    /// <paramref name="userHome"/> stands in for an inherited home that is not set.</summary>
    internal static Dictionary<string, string?> BuildEnvironment(
            IReadOnlyDictionary<string, string?> inherited,
            OpenCodeRunDirectories               dirs,
            OpenCodeHeadlessConfig               config,
            string                               userHome
        ) {
        var env = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var (key, value) in inherited) {
            if (value is not null) env[key] = value;
        }

        env["XDG_CACHE_HOME"] = UserCacheHome(env, userHome);

        env.Remove("OPENCODE_CONFIG");
        env["HOME"]                             = dirs.Home;
        env["OPENCODE_TEST_HOME"]               = dirs.Home;
        if (OperatingSystem.IsWindows()) env["USERPROFILE"] = dirs.Home;
        env["XDG_CONFIG_HOME"]                  = dirs.ConfigHome;
        env["OPENCODE_CONFIG_DIR"]              = dirs.ConfigDir;
        env["XDG_DATA_HOME"]                    = dirs.DataHome;
        env["XDG_STATE_HOME"]                   = dirs.StateHome;
        env["OPENCODE_CONFIG_CONTENT"]          = config.ConfigContent;
        env["OPENCODE_PERMISSION"]              = config.Permission;
        env["OPENCODE_DISABLE_PROJECT_CONFIG"]  = "1";
        env["OPENCODE_DISABLE_CLAUDE_CODE"]     = "1";
        env["OPENCODE_DISABLE_EXTERNAL_SKILLS"] = "1";
        env["OPENCODE_DISABLE_AUTOUPDATE"]      = "1";
        env["OPENCODE_DISABLE_MODELS_FETCH"]    = "1";
        env[HostedAgent.SkipVar]                = "1";

        if (config.AuthContent is { } auth) env["OPENCODE_AUTH_CONTENT"] = auth;

        return env;
    }

    /// <summary>Where OpenCode would put its cache under <paramref name="env"/>: <c>XDG_CACHE_HOME</c>, else
    /// <c>.cache</c> under the OS home (<c>USERPROFILE</c> on Windows, <c>HOME</c> elsewhere).</summary>
    static string UserCacheHome(IReadOnlyDictionary<string, string?> env, string userHome) {
        if (env.GetValueOrDefault("XDG_CACHE_HOME") is { Length: > 0 } cache) return cache;

        var home = env.GetValueOrDefault(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");

        return Path.Combine(string.IsNullOrEmpty(home) ? userHome : home, ".cache");
    }
}
