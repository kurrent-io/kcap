using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Headless;
using Capacitor.Cli.Core.Harness.Pi;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Pi;

/// <summary>A fake <c>pi</c> stands in for the binary: a POSIX script that replays a captured event stream.</summary>
public class PiCliRunnerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string TitleSchema = """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"],"additionalProperties":false}""";

    static readonly string[] Lockdown = [
        "-p", "--mode", "json", "--no-approve", "--no-tools", "--no-extensions", "--no-skills",
        "--no-prompt-templates", "--no-themes", "--no-context-files", "--no-session", "--offline",
    ];

    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "pi-headless", $"{name}.jsonl");

    string StateDir => Path.Combine(Tmp.Path, "state");
    string RunsRoot => Path.Combine(StateDir, "headless-runs");
    HeadlessRunRoot Runs => new(StateDir, "test");

    string FakePi(string body) => Tmp.CreateExecutable($"bin-{Guid.NewGuid():N}/pi", $"#!/bin/sh\n{body}\n");

    string Replaying(string fixturePath) => FakePi($"cat > /dev/null\ncat '{fixturePath}'");

    string ReplayingLines(params string[] lines) => Replaying(Tmp.CreateFile($"events-{Guid.NewGuid():N}.jsonl", string.Join('\n', lines) + "\n"));

    Task<ClaudeCliOutcome> Run(string executable, string prompt = "irrelevant", TimeSpan? timeout = null,
        string? model = null, string? jsonSchema = null, string? systemPrompt = null, CancellationToken ct = default) =>
        PiCliRunner.RunDetailedAsync(prompt, timeout ?? TimeSpan.FromSeconds(20), TimeProvider.System, _ => { }, Runs,
            model, jsonSchema, systemPrompt, executable, ct);

    static string AgentEnd(string? text, string stopReason = "stop") {
        var content = new JsonArray();
        if (text is not null) content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        return new JsonObject {
            ["type"] = "agent_end",
            ["messages"] = new JsonArray(new JsonObject {
                ["role"]       = "assistant",
                ["content"]    = content,
                ["usage"]      = new JsonObject { ["input"] = 10, ["output"] = 2, ["cacheRead"] = 3, ["cacheWrite"] = 1, ["cost"] = new JsonObject { ["total"] = 0.0005 } },
                ["stopReason"] = stopReason,
            }),
        }.ToJsonString();
    }

    async Task RunsRootIsEmpty() =>
        await Assert.That(Directory.Exists(RunsRoot) ? Directory.EnumerateFileSystemEntries(RunsRoot) : []).IsEmpty();

    [Test]
    public async Task BuildArgs_locks_the_run_down_and_replaces_both_system_prompts() {
        await Assert.That(PiCliRunner.BuildArgs("You write titles.", null))
            .IsEquivalentTo([.. Lockdown, "--system-prompt", "You write titles.", "--append-system-prompt", ""], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("  ")]
    public async Task BuildArgs_leaves_the_model_to_pi_when_none_is_given(string? model) {
        await Assert.That(PiCliRunner.BuildArgs(PiCliRunner.NeutralSystemPrompt, model))
            .IsEquivalentTo([.. Lockdown, "--system-prompt", "Answer the request directly.", "--append-system-prompt", ""], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task BuildArgs_passes_a_model_last() {
        await Assert.That(PiCliRunner.BuildArgs("s", "anthropic/claude-haiku-4-5"))
            .IsEquivalentTo([.. Lockdown, "--system-prompt", "s", "--append-system-prompt", "", "--model", "anthropic/claude-haiku-4-5"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task BuildPrompt_appends_the_schema_instruction_only_with_a_schema() {
        await Assert.That(PiCliRunner.BuildPrompt("Name it.", null)).IsEqualTo("Name it.");
        await Assert.That(PiCliRunner.BuildPrompt("Name it.", "  ")).IsEqualTo("Name it.");
        await Assert.That(PiCliRunner.BuildPrompt("Name it.", TitleSchema)).IsEqualTo("Name it.\n\n" + StructuredReply.SchemaInstruction(TitleSchema));
    }

    [Test, ExcludeOn(OS.Windows)]
    [Arguments("unauthorised", nameof(ClaudeCliFailure.NotSignedIn))]
    [Arguments("credit-low", nameof(ClaudeCliFailure.SpendBudget))]
    public async Task A_captured_failure_is_classified_from_its_error_message(string fixture, string expected) {
        var outcome = await Run(Replaying(Fixture(fixture)));

        await Assert.That(outcome.Result).IsNull();
        await Assert.That(outcome.Failure.ToString()).IsEqualTo(expected);
        await Assert.That(outcome.Error!).Contains("\"type\":\"error\"");
    }

    /// <summary>Pi exits 0 here, with an OAuth refresh error for a provider the user configured.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task A_broken_default_provider_is_a_failure_never_a_result() {
        var outcome = await Run(Replaying(Fixture("custom-provider-broken")));

        await Assert.That(outcome.Result).IsNull();
        await Assert.That(outcome.Failure).IsNotNull();
        await Assert.That(outcome.Error!).StartsWith("OAuth refresh failed");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_captured_success_is_a_result_with_its_usage_cost_and_the_model_pi_chose() {
        var outcome = await Run(Replaying(Fixture("success")));

        await Assert.That(outcome.Failure).IsNull();
        var result = outcome.Result!;
        await Assert.That(result.Result).EndsWith("""{"verdict":"fail","reason":"The loop starts at index 1 instead of 0, causing the first element of the list to be excluded from the sum."}""");
        await Assert.That(result.Model).IsEqualTo("claude-haiku-4-5-20251001");
        await Assert.That(result.InputTokens + result.OutputTokens + result.CacheReadTokens + result.CacheWriteTokens).IsEqualTo(4747);
        await Assert.That(Math.Round(result.CostUsd!.Value, 4)).IsEqualTo(0.0061);
        await Assert.That(outcome.Usage).IsNotNull();
        await RunsRootIsEmpty();
    }

    [Test, ExcludeOn(OS.Windows)]
    [Arguments("```json\n{\"title\": \"hello\"}\n```", """{"title":"hello"}""")]
    [Arguments("{\"title\":\"hello\",\"extra\":1}", null)]
    [Arguments("{\"name\":\"hello\"}", null)]
    [Arguments("The title is hello.", null)]
    public async Task A_schema_reply_is_returned_reserialised_only_when_it_matches(string reply, string? expected) {
        var outcome = await Run(ReplayingLines(AgentEnd(reply)), jsonSchema: TitleSchema);

        if (expected is null) {
            await Assert.That(outcome.Result).IsNull();
            await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.OutputUnparseable);
            await Assert.That(outcome.Usage).IsNotNull();
        } else {
            await Assert.That(outcome.Result!.Result).IsEqualTo(expected);
        }
    }

    [Test, ExcludeOn(OS.Windows)]
    [Arguments(null)]
    [Arguments("  \n")]
    public async Task A_stop_with_no_text_is_unparseable(string? text) {
        var outcome = await Run(ReplayingLines(AgentEnd(text)));

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.OutputUnparseable);
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_requested_model_is_reported_as_requested() {
        var outcome = await Run(Replaying(Fixture("success")), model: "anthropic/claude-haiku-4-5");

        await Assert.That(outcome.Result!.Model).IsEqualTo("anthropic/claude-haiku-4-5");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_stop_reason_other_than_stop_or_error_is_a_process_failure() {
        var outcome = await Run(ReplayingLines(AgentEnd("half an ans", "length")));

        await Assert.That(outcome.Result).IsNull();
        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.ProcessFailure);
        await Assert.That(outcome.Error!).Contains("length");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task An_exit_with_no_agent_end_is_classified_from_stderr() {
        var outcome = await Run(FakePi("cat > /dev/null\necho 'No models available. Use /login or set an API key.' >&2\nexit 1"));

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.NotSignedIn);
        await Assert.That(outcome.Error!).Contains("No models available");
    }

    [Test, ExcludeOn(OS.Windows)]
    [Arguments("""{"type":"object","patternProperties":{"^x":{"type":"string"}}}""", "patternProperties")]
    [Arguments("""{"type":""", "JSON")]
    public async Task A_schema_the_reply_cannot_be_checked_against_refuses_before_pi_starts(string schema, string named) {
        var marker = Path.Combine(Tmp.Path, "started");
        var outcome = await Run(FakePi($"touch '{marker}'"), jsonSchema: schema);

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.ProcessFailure);
        await Assert.That(outcome.Error!).Contains(named);
        await Assert.That(File.Exists(marker)).IsFalse();
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task A_run_root_that_is_not_private_refuses_before_pi_starts() {
        Directory.CreateDirectory(RunsRoot);
        File.SetUnixFileMode(RunsRoot, (UnixFileMode)0b111_111_111);
        var marker = Path.Combine(Tmp.Path, "started");

        var outcome = await Run(FakePi($"touch '{marker}'"));

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.ProcessFailure);
        await Assert.That(outcome.Error!).Contains("owner-only");
        await Assert.That(File.Exists(marker)).IsFalse();
    }

    /// <summary>The script records what it was given, then answers, so the run directory it saw can be checked
    /// for removal afterwards.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task Pi_gets_the_prompt_on_stdin_as_utf8_without_a_bom_in_an_empty_private_directory() {
        var seen = Tmp.CreateDir("seen").Path;
        var pi = FakePi($"""
            cat > '{seen}/stdin'
            pwd > '{seen}/cwd'
            ls -A > '{seen}/listing'
            printf '%s' "$KCAP_SKIP" > '{seen}/skip'
            cat '{Tmp.CreateFile("answer.jsonl", AgentEnd("ok") + "\n")}'
            """);
        const string prompt = "Grüße, 世界 — naïve café ✓";

        var outcome = await Run(pi, prompt, jsonSchema: null);

        await Assert.That(outcome.Result!.Result).IsEqualTo("ok");
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(seen, "stdin"))).IsEquivalentTo(new UTF8Encoding(false).GetBytes(prompt), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(seen, "listing"))).IsEmpty();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(seen, "skip"))).IsEqualTo("1");
        var cwd = (await File.ReadAllTextAsync(Path.Combine(seen, "cwd"))).Trim();
        await Assert.That(Path.GetFileName(cwd)).StartsWith("pi-test-");
        await Assert.That(Directory.Exists(cwd)).IsFalse();
        await RunsRootIsEmpty();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task The_deadline_kills_pi_keeps_the_usage_reported_so_far_and_removes_the_directory() {
        var partial = File.ReadLines(Fixture("success")).First(l => l.Contains("\"type\":\"message_end\"") && l.Contains("\"stopReason\":\"toolUse\""));
        var pi = FakePi($"cat > /dev/null\ncat '{Tmp.CreateFile("partial.jsonl", partial + "\n")}'\nexec sleep 30");

        var outcome = await Run(pi, timeout: TimeSpan.FromSeconds(1));

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.Timeout);
        await Assert.That(outcome.Usage!.InputTokens).IsEqualTo(2193);
        await RunsRootIsEmpty();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Cancellation_kills_pi_and_removes_the_directory_before_it_propagates() {
        var started = Path.Combine(Tmp.Path, "started");
        var pi = FakePi($"cat > /dev/null\ntouch '{started}'\nexec sleep 30");
        using var cts = new CancellationTokenSource();

        var run = Run(pi, ct: cts.Token);
        var waited = Environment.TickCount64;
        while (!File.Exists(started) && Environment.TickCount64 - waited < 10_000) await Task.Delay(20);
        await cts.CancelAsync();

        await Assert.That(async () => await run).Throws<OperationCanceledException>();
        await RunsRootIsEmpty();
    }
}
