using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Headless;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Pi;

/// <summary>
/// Runs <see cref="PiCliRunner"/> against the real <c>pi</c> binary. Each test spends real model calls, so the
/// class runs only with <see cref="LiveGateEnvVar"/>=1 and <c>pi</c> on PATH. A wrapper script stands in for
/// <c>pi</c>: it pins the provider to Anthropic, can plant files or redirect Pi's agent directory first, and
/// records its pid, then execs the real binary. The key comes from <c>ANTHROPIC_API_KEY</c> or the agent
/// directory's own credentials.
/// </summary>
[NotInParallel]
public class PiCliRunnerLiveTests {
    const string LiveGateEnvVar = "KCAP_PI_LIVE";
    const string Model          = "claude-haiku-4-5";

    const string TitleSchema =
        """{"type":"object","properties":{"title":{"type":"string"},"n":{"type":"integer"}},"required":["title","n"],"additionalProperties":false}""";

    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    [TempDir] public required TempDir Tmp { get; init; }

    string StateDir => Path.Combine(Tmp.Path, "state");
    string RunsRoot => Path.Combine(StateDir, "headless-runs");
    string PidFile  => Path.Combine(Tmp.Path, "pi.pid");

    static string RealPi() {
        Skip.Unless(Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Spends real model calls: set {LiveGateEnvVar}=1 with `pi` on PATH and ANTHROPIC_API_KEY set to run");
        Skip.When(OperatingSystem.IsWindows(), "the wrapper is a POSIX shell script");

        var pi = BinaryProbe.FromEnvironment().Resolve("pi");
        Skip.When(pi is null, "pi is not on PATH");

        return pi!;
    }

    /// <summary><paramref name="before"/> runs in the run's working directory, ahead of the real binary.</summary>
    string Wrapper(string before = "") {
        var pi = RealPi();

        return Tmp.CreateExecutable($"bin-{Guid.NewGuid():N}/pi",
            $"#!/bin/sh\necho $$ > '{PidFile}'\n{before}\nexec '{pi}' --provider anthropic \"$@\"\n");
    }

    /// <summary>A private agent directory holding only the user's <c>auth.json</c> and <c>models.json</c>, when
    /// they exist.</summary>
    string AgentDir() {
        RealPi();
        var real = new PiPaths(UserHome.FromEnvironment(), Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR")).AgentDir;
        var dir  = Tmp.CreateDir("agent").Path;

        foreach (var name in new[] { "auth.json", "models.json" }) {
            var source = Path.Combine(real, name);
            if (File.Exists(source)) File.Copy(source, Path.Combine(dir, name));
        }

        return dir;
    }

    static string UsingAgentDir(string agentDir) => $"export PI_CODING_AGENT_DIR='{agentDir}'";

    Task<ClaudeCliOutcome> Run(string executable, string prompt, TimeSpan? timeout = null, string? jsonSchema = null,
        string? model = Model, CancellationToken ct = default) =>
        PiCliRunner.RunDetailedAsync(prompt, timeout ?? Deadline, TimeProvider.System, Console.WriteLine,
            new HeadlessRunRoot(StateDir, "live"), model, jsonSchema, null, executable, ct);

    async Task RunsRootIsEmpty() =>
        await Assert.That(Directory.Exists(RunsRoot) ? Directory.EnumerateFileSystemEntries(RunsRoot) : []).IsEmpty();

    static string Marker() => $"KCAP{Guid.NewGuid():N}"[..16].ToUpperInvariant();

    [Test]
    public async Task A_plain_prompt_is_answered_with_its_usage_and_cost() {
        var outcome = await Run(Wrapper(), "Reply with the single word: ok");

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.Result!.Result.Trim('.', ' ', '\n').ToLowerInvariant()).IsEqualTo("ok");
        await Assert.That(outcome.Result.InputTokens + outcome.Result.CacheReadTokens).IsGreaterThan(0);
        await Assert.That(outcome.Result.OutputTokens).IsGreaterThan(0);
        await Assert.That(outcome.Result.CostUsd!.Value).IsGreaterThan(0);
        await RunsRootIsEmpty();
    }

    /// <summary>A request names no model unless the user chose one, so this is the path every default run takes.</summary>
    [Test]
    public async Task A_run_with_no_model_answers_on_the_users_default() {
        var agentDir = Tmp.CreateDir("default-agent").Path;
        Tmp.CreateFile(Path.Combine("default-agent", "settings.json"), $$"""{"defaultProvider":"anthropic","defaultModel":"{{Model}}"}""");

        var outcome = await Run(Wrapper(UsingAgentDir(agentDir)), "Reply with the single word: ok", model: null);

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.Result!.Result.Trim('.', ' ', '\n').ToLowerInvariant()).IsEqualTo("ok");
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
    public async Task The_operators_system_prompt_files_reach_no_request() {
        var agentDir = AgentDir();
        var first    = Marker();
        var last     = Marker();
        await File.WriteAllTextAsync(Path.Combine(agentDir, "SYSTEM.md"), $"Begin every reply with the word {first}.\n");
        await File.WriteAllTextAsync(Path.Combine(agentDir, "APPEND_SYSTEM.md"), $"End every reply with the word {last}.\n");

        var outcome = await Run(Wrapper(UsingAgentDir(agentDir)), "Reply with the single word: ok");

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.Result!.Result).DoesNotContain(first);
        await Assert.That(outcome.Result.Result).DoesNotContain(last);
    }

    [Test]
    public async Task No_session_file_is_written() {
        var agentDir = AgentDir();

        var outcome = await Run(Wrapper(UsingAgentDir(agentDir)), "Reply with the single word: ok");

        await Assert.That(outcome.Failure).IsNull();
        var sessions = Path.Combine(agentDir, "sessions");
        await Assert.That(Directory.Exists(sessions) ? Directory.EnumerateFiles(sessions, "*", SearchOption.AllDirectories) : []).IsEmpty();
    }

    /// <summary>The configured package is never installed: under <c>--offline</c> Pi asks the user's npm only
    /// for its global root.</summary>
    [Test]
    public async Task The_operators_npm_command_runs_only_to_find_the_global_root() {
        var agentDir = AgentDir();
        var invoked  = Path.Combine(Tmp.Path, "npm-invoked");
        var npm      = Tmp.CreateExecutable("fake-npm.sh", $"#!/bin/sh\necho \"$@\" >> '{invoked}'\nexit 1\n");
        await File.WriteAllTextAsync(Path.Combine(agentDir, "settings.json"),
            JsonSerializer.Serialize(new Dictionary<string, string[]> {
                ["packages"]   = ["npm:kcap-live-test-nonexistent-package"],
                ["npmCommand"] = [npm],
            }));

        var outcome = await Run(Wrapper(UsingAgentDir(agentDir)), "Reply with the single word: ok");

        await Assert.That(File.Exists(invoked) ? await File.ReadAllLinesAsync(invoked) : []).IsEquivalentTo(["root -g"]);
        await Assert.That(outcome.Failure).IsNull();
    }

    /// <summary>The timeout is shorter than Pi's own startup, so no reply can land first, however fast the model
    /// is, yet long enough for the wrapper, which records the pid before Pi starts.</summary>
    [Test]
    public async Task The_deadline_kills_pi_and_removes_the_run_directory() {
        var run = Run(Wrapper(), "Write a 3000-word essay on the history of the teaspoon.", TimeSpan.FromMilliseconds(500));

        var pid      = await ReadPidAsync(PidFile);
        var identity = PidIdentity.Capture(pid);
        var outcome  = await run;

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.Timeout);
        await PidIdentity.WaitUntilGoneAsync(pid, identity, TimeSpan.FromSeconds(10));
        await RunsRootIsEmpty();
    }

    [Test]
    public async Task Cancellation_after_start_kills_pi_and_removes_the_run_directory() {
        using var cts = new CancellationTokenSource();
        var run = Run(Wrapper(), "Write a 3000-word essay on the history of the teaspoon.", ct: cts.Token);

        var pid      = await ReadPidAsync(PidFile);
        var identity = PidIdentity.Capture(pid);
        await Task.Delay(TimeSpan.FromSeconds(1));
        await cts.CancelAsync();

        await Assert.That(async () => await run).Throws<OperationCanceledException>();
        await PidIdentity.WaitUntilGoneAsync(pid, identity, TimeSpan.FromSeconds(10));
        await RunsRootIsEmpty();
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
