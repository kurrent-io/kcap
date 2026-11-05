using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Headless;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.OpenCode;

/// <summary>
/// Runs <see cref="OpenCodeCliRunner"/> against the real <c>opencode</c> binary. Each test spends real model calls,
/// so the class runs only with <see cref="LiveGateEnvVar"/>=1, <c>opencode</c> on PATH and <c>ANTHROPIC_API_KEY</c>
/// set. The inherited environment points OpenCode's config and data homes at empty temporary directories, so
/// nothing of the user's OpenCode is read. A wrapper script stands in for <c>opencode</c>: it can plant files in
/// the run's working directory first and record its pid, then runs the real binary.
/// </summary>
[NotInParallel]
public class OpenCodeCliRunnerLiveTests {
    const string LiveGateEnvVar = "KCAP_OPENCODE_LIVE";
    const string Model          = "anthropic/claude-haiku-4-5";

    const string TitleSchema =
        """{"type":"object","properties":{"title":{"type":"string"},"n":{"type":"integer"}},"required":["title","n"],"additionalProperties":false}""";

    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    [TempDir] public required TempDir Tmp { get; init; }

    string StateDir   => Path.Combine(Tmp.Path, "state");
    string RunsRoot   => Path.Combine(StateDir, "headless-runs");
    string PidFile    => Path.Combine(Tmp.Path, "opencode.pid");
    string UserConfig => Path.Combine(Tmp.Path, "user-config");
    string UserData   => Path.Combine(Tmp.Path, "user-data");

    static string RealOpenCode() {
        Skip.Unless(Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Spends real model calls: set {LiveGateEnvVar}=1 with `opencode` on PATH and ANTHROPIC_API_KEY set to run");
        Skip.When(OperatingSystem.IsWindows(), "the wrapper is a POSIX shell script");
        Skip.When(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")), "ANTHROPIC_API_KEY is not set");

        var opencode = BinaryProbe.FromEnvironment().Resolve("opencode");
        Skip.When(opencode is null, "opencode is not on PATH");

        return opencode!;
    }

    /// <summary><paramref name="before"/> runs in the run's working directory ahead of the real binary, and
    /// <paramref name="after"/> once it has exited, the wrapper then exiting with its status.</summary>
    string Wrapper(string before = "", string after = "") {
        var opencode = RealOpenCode();

        return Tmp.CreateExecutable($"bin-{Guid.NewGuid():N}/opencode",
            $"#!/bin/sh\necho $$ > '{PidFile}'\n{before}\n'{opencode}' \"$@\"\nstatus=$?\n{after}\nexit $status\n");
    }

    Dictionary<string, string?> Inherited() {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string?)e.Value;

        env.Remove("OPENCODE_CONFIG");
        env.Remove("OPENCODE_CONFIG_DIR");
        env.Remove("OPENCODE_CONFIG_CONTENT");
        env.Remove("OPENCODE_PERMISSION");
        env.Remove("OPENCODE_AUTH_CONTENT");
        env["XDG_CONFIG_HOME"] = Directory.CreateDirectory(UserConfig).FullName;
        env["XDG_DATA_HOME"]   = Directory.CreateDirectory(UserData).FullName;

        return env;
    }

    OpenCodePaths Paths => new(new(Tmp.Path), null, UserConfig, UserData);

    Task<ClaudeCliOutcome> Run(string executable, string prompt, TimeSpan? timeout = null, string? jsonSchema = null,
        IReadOnlyDictionary<string, string?>? env = null, string? model = Model, CancellationToken ct = default) =>
        OpenCodeCliRunner.RunDetailedAsync(prompt, timeout ?? Deadline, TimeProvider.System, Console.WriteLine,
            new HeadlessRunRoot(StateDir, "live"), Paths, env ?? Inherited(), model, jsonSchema, null, executable, ct);

    async Task RunsRootIsEmpty() =>
        await Assert.That(Directory.Exists(RunsRoot) ? Directory.EnumerateFileSystemEntries(RunsRoot) : []).IsEmpty();

    static string Marker() => $"KCAP{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    /// <summary>Every tool off and every permission denied must still answer a plain prompt.</summary>
    [Test]
    public async Task A_plain_prompt_is_answered_with_its_usage_and_cost() {
        var outcome = await Run(Wrapper(), "Reply with the single word: ok");

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.Result!.Result.Trim('.', ' ', '\n').ToLowerInvariant()).IsEqualTo("ok");
        await Assert.That(outcome.Result.InputTokens + outcome.Result.CacheReadTokens + outcome.Result.CacheWriteTokens).IsGreaterThan(0);
        await Assert.That(outcome.Result.OutputTokens).IsGreaterThan(0);
        await Assert.That(outcome.Result.CostUsd!.Value).IsGreaterThan(0);
        await RunsRootIsEmpty();
    }

    /// <summary>Fake Bedrock credentials in the user's home: AWS rejecting them proves they reached the SDK, and with no
    /// such file the SDK finds nothing to send. Neither call reaches a model or needs an AWS account.</summary>
    [Test]
    public async Task A_home_file_aws_sign_in_reaches_bedrock_through_the_private_home() {
        const string bedrock = "amazon-bedrock/anthropic.claude-haiku-4-5-20251001-v1:0";

        Dictionary<string, string?> EnvWithHome(string home) {
            var env = Inherited();
            foreach (var name in env.Keys.Where(k => k.StartsWith("AWS_", StringComparison.Ordinal)).ToList()) env.Remove(name);
            env["HOME"]        = home;
            env["AWS_PROFILE"] = "default";
            env["AWS_REGION"]  = "us-east-1";

            return env;
        }

        var bare = await Run(Wrapper(), "ok", env: EnvWithHome(Tmp.CreateDir("bare-home").Path), model: bedrock);
        await Assert.That(bare.Error).Contains("Could not load credentials");
        await Assert.That(bare.Failure).IsEqualTo(ClaudeCliFailure.NotSignedIn);

        var home = Tmp.CreateDir("aws-home").Path;
        Tmp.CreateFile(Path.Combine("aws-home", ".aws", "credentials"),
            "[default]\naws_access_key_id=AKIAFAKEFAKEFAKEFAKE\naws_secret_access_key=fakefakefakefakefakefakefakefakefakefake\n");

        var signedIn = await Run(Wrapper(), "ok", env: EnvWithHome(home), model: bedrock);
        await Assert.That(signedIn.Error).Contains("security token");
        await Assert.That(signedIn.Failure).IsEqualTo(ClaudeCliFailure.NotSignedIn);
    }

    /// <summary>A request names no model unless the user chose one, so this is the path every default run takes. The
    /// model is the user's own, from their config.</summary>
    [Test]
    public async Task A_run_with_no_model_answers_on_the_users_default() {
        var env = Inherited();
        Tmp.CreateFile(Path.Combine("user-config", "opencode", "opencode.json"), $$"""{"model":"{{Model}}"}""");

        var outcome = await Run(Wrapper(), "Reply with the single word: ok", env: env, model: null);

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.Result!.Result.Trim('.', ' ', '\n').ToLowerInvariant()).IsEqualTo("ok");
    }

    /// <summary>The shape of a judge's request: a long transcript with a schema to answer in, which a model given the
    /// prompt as a file answers in prose.</summary>
    [Test]
    public async Task A_schema_answer_over_a_long_transcript_comes_back_as_the_matched_object() {
        var transcript = string.Concat(Enumerable.Range(0, 600).Select(i => $"user: step {i}: fix the redirect\nassistant: edited auth.ts, tests pass\n"));

        var outcome = await Run(Wrapper(), $"Score this session.\n<transcript>\n{transcript}</transcript>\nUse title for a short summary and n for a score from 1 to 5.",
            jsonSchema: TitleSchema);

        await Assert.That(outcome.Failure).IsNull();
        using var doc = JsonDocument.Parse(outcome.Result!.Result);
        await Assert.That(doc.RootElement.GetProperty("n").GetInt32()).IsBetween(1, 5);
    }

    [Test]
    public async Task A_schema_answer_comes_back_as_the_matched_object() {
        var outcome = await Run(Wrapper(), "Reply with the title \"hello\" and n = 7.", jsonSchema: TitleSchema);

        await Assert.That(outcome.Failure).IsNull();
        using var doc = JsonDocument.Parse(outcome.Result!.Result);
        await Assert.That(doc.RootElement.GetProperty("title").GetString()).IsEqualTo("hello");
        await Assert.That(doc.RootElement.GetProperty("n").GetInt32()).IsEqualTo(7);
    }

    [Test]
    public async Task A_non_ascii_prompt_reaches_the_model_exactly() {
        const string text = "Grüße, 世界 — naïve café ✓";

        var outcome = await Run(Wrapper(), $"Repeat the following text exactly, with nothing before or after it:\n{text}");

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.Result!.Result.Trim()).IsEqualTo(text);
    }

    /// <summary>The marker sits at the very end, so the answer proves the whole prompt arrived.</summary>
    [Test]
    public async Task A_prompt_of_forty_thousand_characters_is_answered() {
        var marker = Marker();
        const string ask = "\n\nIgnore the filler above. Reply with only the code word that follows: ";
        var filler = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 40_000 / 45 + 1));
        var prompt = filler[..(40_000 - ask.Length - marker.Length)] + ask + marker;

        var outcome = await Run(Wrapper(), prompt);

        await Assert.That(prompt.Length).IsEqualTo(40_000);
        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.Result!.Result).Contains(marker);
    }

    /// <summary>The answer is the evidence a tool did not run: with a read tool, the model would quote the file.</summary>
    [Test]
    public async Task A_file_in_the_working_directory_is_not_read() {
        var marker = Marker();

        var outcome = await Run(Wrapper($"printf '%s' '{marker}' > notes.txt"),
            "Read the file notes.txt in the current directory and reply with its exact contents. "
          + "If you cannot read it, reply with the single word: unavailable");

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.Result!.Result).DoesNotContain(marker);
    }

    [Test]
    public async Task An_operators_allow_all_permission_does_not_let_the_run_write_a_file() {
        var listing = Path.Combine(Tmp.Path, "listing");
        var env     = Inherited();
        env["OPENCODE_PERMISSION"] = """{"*":"allow"}""";

        var outcome = await Run(Wrapper(after: $"ls -A > '{listing}'"),
            "Create a file named out.txt in the current directory containing the word done, then reply with the single word: finished",
            env: env);

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That((await File.ReadAllTextAsync(listing)).Trim()).IsEmpty();
    }

    [Test]
    public async Task An_mcp_server_in_an_inherited_config_file_never_starts() {
        var env = Inherited();
        env["OPENCODE_CONFIG"] = Tmp.CreateFile("operator.json", McpConfig(out var marker));

        await AssertTheServerStartsOnlyOutsideTheRun(env, marker);
    }

    /// <summary><c>OPENCODE_CONFIG_DIR</c> alone adds a layer and leaves <c>$XDG_CONFIG_HOME/opencode</c> loading,
    /// so this is the case that needs the run's own config home.</summary>
    [Test]
    public async Task An_mcp_server_in_the_users_global_config_never_starts() {
        var env = Inherited();
        Tmp.CreateFile(Path.Combine("user-config", "opencode", "opencode.json"), McpConfig(out var marker));

        await AssertTheServerStartsOnlyOutsideTheRun(env, marker);
    }

    /// <summary>OpenCode loads <c>.opencode/opencode.json</c> under its home whatever else is set. The control is the
    /// run's own environment with the user's home put back, where <c>opencode debug config --pure</c> (no model call)
    /// resolves both planted entries: without it, a layer OpenCode never read would pass. In the run, the same command
    /// resolves neither, the server never starts and the instruction never reaches the reply.</summary>
    [Test]
    public async Task An_mcp_server_and_instructions_in_the_home_opencode_config_never_reach_the_run() {
        var opencode     = RealOpenCode();
        var home         = Tmp.CreateDir("user-home").Path;
        var word         = Marker();
        var instructions = Tmp.CreateFile("planted-instructions.md", $"End every reply with the code word {word}.");
        var planted      = JsonNode.Parse(McpConfig(out var marker))!.AsObject();
        planted["instructions"] = new JsonArray((JsonNode?)instructions);
        Tmp.CreateFile(Path.Combine("user-home", ".opencode", "opencode.json"), planted.ToJsonString());

        var env = Inherited();
        env["HOME"] = home;
        env.Remove("OPENCODE_TEST_HOME");

        var control = OpenCodeCliRunner.BuildEnvironment(env,
            new(home, Tmp.CreateDir("control", "xdg-config").Path, Tmp.CreateDir("control", "config").Path,
                Tmp.CreateDir("control", "data").Path, Tmp.CreateDir("control", "state").Path),
            OpenCodeHeadlessConfig.Build(Paths, env), home);
        var resolved = ResolvedConfig(await RunControlAsync(opencode, ["debug", "config", "--pure"], control));
        await Assert.That(resolved["mcp"]?["marker"]).IsNotNull();
        await Assert.That(resolved["instructions"]?.ToJsonString() ?? "").Contains("planted-instructions.md");

        var dump    = Path.Combine(Tmp.Path, "run-config.json");
        var outcome = await Run(Wrapper(after: $"'{opencode}' debug config --pure > '{dump}' 2>/dev/null"),
            "Reply with the single word: ok", env: env);

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.Result!.Result).DoesNotContain(word);
        await Assert.That(File.Exists(marker)).IsFalse();
        var inRun = ResolvedConfig(await File.ReadAllTextAsync(dump));
        await Assert.That(inRun["mcp"]?["marker"]).IsNull();
        await Assert.That(inRun["instructions"]?.ToJsonString() ?? "").DoesNotContain("planted-instructions.md");
    }

    /// <summary>The JSON object <c>opencode debug config</c> prints, after any log lines before it.</summary>
    static JsonObject ResolvedConfig(string output) => JsonNode.Parse(output[output.IndexOf('{')..])!.AsObject();

    /// <summary>A config whose one MCP server touches <paramref name="marker"/> when it starts.</summary>
    string McpConfig(out string marker) {
        marker = Path.Combine(Tmp.Path, $"mcp-started-{Guid.NewGuid():N}");
        var server = Tmp.CreateExecutable($"mcp-server-{Guid.NewGuid():N}.sh", $"#!/bin/sh\ntouch '{marker}'\nexec sleep 30\n");

        return new JsonObject {
            ["mcp"] = new JsonObject {
                ["marker"] = new JsonObject {
                    ["type"]    = "local",
                    ["command"] = new JsonArray((JsonNode?)server),
                    ["enabled"] = true,
                },
            },
        }.ToJsonString();
    }

    /// <summary>The control opens the same environment outside the runner with <c>opencode mcp list</c> (no model
    /// call), which starts the server: without it, a server that never starts anywhere would pass.</summary>
    async Task AssertTheServerStartsOnlyOutsideTheRun(Dictionary<string, string?> env, string marker) {
        var opencode = RealOpenCode();

        await RunControlAsync(opencode, ["mcp", "list"], env);
        await Assert.That(File.Exists(marker)).IsTrue();
        File.Delete(marker);

        var outcome = await Run(Wrapper(), "Reply with the single word: ok", env: env);

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(File.Exists(marker)).IsFalse();
    }

    /// <summary>The timeout is shorter than OpenCode's own startup, so no reply can land first, however fast the
    /// model is.</summary>
    [Test]
    public async Task The_deadline_kills_opencode_and_removes_every_directory() {
        var run = Run(Wrapper(), "Write a 3000-word essay on the history of the teaspoon.", TimeSpan.FromSeconds(1));

        var pid      = await ReadPidAsync(PidFile);
        var identity = PidIdentity.Capture(pid);
        var outcome  = await run;

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.Timeout);
        await PidIdentity.WaitUntilGoneAsync(pid, identity, TimeSpan.FromSeconds(10));
        await RunsRootIsEmpty();
    }

    [Test]
    public async Task Cancellation_after_start_kills_opencode_and_removes_every_directory() {
        using var cts = new CancellationTokenSource();
        var run = Run(Wrapper(), "Write a 3000-word essay on the history of the teaspoon.", ct: cts.Token);

        var pid      = await ReadPidAsync(PidFile);
        var identity = PidIdentity.Capture(pid);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();

        await Assert.That(async () => await run).Throws<OperationCanceledException>();
        await PidIdentity.WaitUntilGoneAsync(pid, identity, TimeSpan.FromSeconds(10));
        await RunsRootIsEmpty();
    }

    /// <summary>Runs <paramref name="opencode"/> with <paramref name="args"/> in an empty directory, killing it
    /// after a bound, and returns what it printed.</summary>
    async Task<string> RunControlAsync(string opencode, string[] args, IReadOnlyDictionary<string, string?> env) {
        var psi = new ProcessStartInfo(opencode) {
            WorkingDirectory       = Tmp.CreateDir($"control-{Guid.NewGuid():N}").Path,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.Environment.Clear();
        foreach (var (key, value) in env) psi.Environment[key] = value;
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        try {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        } catch (TimeoutException) {
            process.Kill(entireProcessTree: true);
        }

        return await stdout;
    }

    static async Task<int> ReadPidAsync(string pidFile) {
        var start = Environment.TickCount64;

        while (Environment.TickCount64 - start < 10_000) {
            if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out var pid)) return pid;

            await Task.Delay(20);
        }

        throw new TimeoutException($"the wrapper never wrote {pidFile}");
    }
}
