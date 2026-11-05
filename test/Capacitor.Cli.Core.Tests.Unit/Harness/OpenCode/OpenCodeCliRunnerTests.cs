using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Headless;
using Capacitor.Cli.Core.Harness.OpenCode;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.OpenCode;

/// <summary>A fake <c>opencode</c> stands in for the binary: a POSIX script that replays a captured event stream.</summary>
public class OpenCodeCliRunnerTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string TitleSchema = """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"],"additionalProperties":false}""";

    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "opencode-headless", name);

    string StateDir => Path.Combine(Tmp.Path, "state");
    string RunsRoot => Path.Combine(StateDir, "headless-runs");
    HeadlessRunRoot Runs => new(StateDir, "test");

    string UserData => Path.Combine(Tmp.Path, "user-data");
    OpenCodePaths Paths => new(new(Tmp.Path), Path.Combine(Tmp.Path, "user-config"), null, UserData);

    /// <summary>Built from fixed values, so nothing the developer exports reaches a test.</summary>
    Dictionary<string, string?> DaemonEnvironment() => new(StringComparer.Ordinal) {
        ["PATH"]            = "/usr/bin:/bin",
        ["HOME"]            = Tmp.Path,
        ["XDG_CONFIG_HOME"] = Path.Combine(Tmp.Path, "user-config-home"),
        ["XDG_DATA_HOME"]   = UserData,
    };

    string FakeOpenCode(string body) => Tmp.CreateExecutable($"bin-{Guid.NewGuid():N}/opencode", $"#!/bin/sh\n{body}\n");

    string Replaying(string fixturePath, int exitCode = 0) => FakeOpenCode($"cat '{fixturePath}'\nexit {exitCode}");

    string ReplayingLines(params string[] lines) => Replaying(Tmp.CreateFile($"events-{Guid.NewGuid():N}.jsonl", string.Join('\n', lines) + "\n"));

    Task<ClaudeCliOutcome> Run(string executable, string prompt = "irrelevant", TimeSpan? timeout = null,
        string? model = null, string? jsonSchema = null, string? systemPrompt = null,
        IReadOnlyDictionary<string, string?>? env = null, Action<string>? log = null, CancellationToken ct = default) =>
        OpenCodeCliRunner.RunDetailedAsync(prompt, timeout ?? TimeSpan.FromSeconds(20), TimeProvider.System, log ?? (_ => { }), Runs,
            Paths, env ?? DaemonEnvironment(), model, jsonSchema, systemPrompt, executable, ct);

    static string Reply(string text) => new JsonObject {
        ["type"] = "text",
        ["part"] = new JsonObject { ["type"] = "text", ["text"] = text },
    }.ToJsonString();

    static readonly OpenCodeRunDirectories Dirs = new("/run/home", "/run/xdg-config", "/run/config", "/run/data", "/run/state");

    const string StepFinish =
        """{"type":"step_finish","part":{"tokens":{"input":10,"output":2,"reasoning":0,"cache":{"read":3,"write":1}},"cost":0.0005}}""";

    async Task RunsRootIsEmpty() =>
        await Assert.That(Directory.Exists(RunsRoot) ? Directory.EnumerateFileSystemEntries(RunsRoot) : []).IsEmpty();

    [Test]
    public async Task BuildArgs_runs_pure_json_with_error_logs_and_never_names_a_prompt() {
        await Assert.That(OpenCodeCliRunner.BuildArgs(null)).IsEquivalentTo(
            ["run", "--format", "json", "--pure", "--print-logs", "--log-level", "ERROR"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task BuildArgs_adds_a_model_last() {
        await Assert.That(OpenCodeCliRunner.BuildArgs("anthropic/claude-haiku-4-5")).IsEquivalentTo(
            ["run", "--format", "json", "--pure", "--print-logs", "--log-level", "ERROR", "-m", "anthropic/claude-haiku-4-5"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("")]
    [Arguments("  ")]
    public async Task BuildArgs_leaves_a_blank_model_to_opencode(string model) =>
        await Assert.That(OpenCodeCliRunner.BuildArgs(model)).DoesNotContain("-m");

    /// <summary>Where OpenCode finds the user's default when no config names one, and the only state a run takes.</summary>
    [Test]
    public async Task The_users_recent_models_and_nothing_else_reach_the_private_state() {
        var user = Tmp.CreateDir("user-state").Path;
        Tmp.CreateFile(Path.Combine("user-state", "opencode", "model.json"), "recent");
        Tmp.CreateFile(Path.Combine("user-state", "opencode", "kv.json"), "other");
        var run = Tmp.CreateDir("run-state").Path;

        OpenCodeCliRunner.CopyRecentModels(new Dictionary<string, string?> { ["XDG_STATE_HOME"] = user }, "/nowhere", run, _ => { });

        await Assert.That(Directory.GetFiles(Path.Combine(run, "opencode")).Select(f => Path.GetFileName(f))).IsEquivalentTo(["model.json"]);
        await Assert.That(File.ReadAllText(Path.Combine(run, "opencode", "model.json"))).IsEqualTo("recent");
    }

    /// <summary>A cloud sign-in is usable from the run, and removing the run leaves the user's own files alone.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task The_users_cloud_sign_ins_reach_the_run_and_survive_its_removal() {
        var seen = Tmp.CreateDir("seen").Path;
        var aws  = Tmp.CreateFile(Path.Combine("user-home", ".aws", "credentials"), "aws");
        var adc  = Tmp.CreateFile(Path.Combine("user-home", ".config", "gcloud", "application_default_credentials.json"), "gcloud");
        var env  = DaemonEnvironment();
        env["HOME"] = Tmp.PathTo("user-home");
        var opencode = FakeOpenCode($"""
            cat "$HOME/.aws/credentials" > '{seen}/aws'
            cat "$HOME/.config/gcloud/application_default_credentials.json" > '{seen}/gcloud'
            ls -A "$HOME/.config" > '{seen}/config-listing'
            echo '{Reply("ok")}'
            echo '{StepFinish}'
            """);

        var outcome = await Run(opencode, "Name it.", env: env);

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(seen, "aws"))).IsEqualTo("aws");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(seen, "gcloud"))).IsEqualTo("gcloud");
        await Assert.That((await File.ReadAllTextAsync(Path.Combine(seen, "config-listing"))).Trim()).IsEqualTo("gcloud");
        await Assert.That(File.Exists(aws)).IsTrue();
        await Assert.That(File.Exists(adc)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_user_with_no_cloud_sign_in_gets_an_empty_private_home() {
        var seen = Tmp.CreateDir("seen").Path;
        var env  = DaemonEnvironment();
        env["HOME"] = Tmp.CreateDir("bare-home").Path;
        var opencode = FakeOpenCode($"""
            ls -A "$HOME" > '{seen}/listing'
            echo '{Reply("ok")}'
            echo '{StepFinish}'
            """);

        await Run(opencode, "Name it.", env: env);

        await Assert.That((await File.ReadAllTextAsync(Path.Combine(seen, "listing"))).Trim()).IsEmpty();
    }

    [Test]
    public async Task The_credential_home_prefers_HOME_as_the_aws_sdk_does() {
        await Assert.That(OpenCodeCliRunner.CredentialHome(new Dictionary<string, string?> { ["HOME"] = "/h", ["USERPROFILE"] = "/u" }, "/fallback")).IsEqualTo("/h");
        await Assert.That(OpenCodeCliRunner.CredentialHome(new Dictionary<string, string?>(), "/fallback")).IsEqualTo("/fallback");
    }

    /// <summary>The whole directory, so an SSO token cache is there too, and removing the run leaves it alone.</summary>
    [Test]
    public async Task The_aws_directory_reaches_the_private_home_and_survives_its_removal() {
        var user = Tmp.CreateDir("user-home").Path;
        var file = Tmp.CreateFile(Path.Combine("user-home", ".aws", "sso", "cache", "token.json"), "token");
        var home = Tmp.CreateDir("private-home").Path;

        OpenCodeHomeCredentials.Apply(user, home, _ => { });

        await Assert.That(File.ReadAllText(Path.Combine(home, ".aws", "sso", "cache", "token.json"))).IsEqualTo("token");

        NoFollowTree.Delete(home);

        await Assert.That(File.Exists(file)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task The_users_recent_models_reach_the_run_under_its_own_state_home() {
        var seen = Tmp.CreateDir("seen").Path;
        Tmp.CreateFile(Path.Combine("user-state", "opencode", "model.json"), "recent");
        var env = DaemonEnvironment();
        env["XDG_STATE_HOME"] = Tmp.PathTo("user-state");
        var opencode = FakeOpenCode($"""
            cat "$XDG_STATE_HOME/opencode/model.json" > '{seen}/model'
            printf '%s' "$XDG_STATE_HOME" > '{seen}/state-home'
            echo '{Reply("ok")}'
            echo '{StepFinish}'
            """);

        var outcome = await Run(opencode, "Name it.", env: env);

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(seen, "model"))).IsEqualTo("recent");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(seen, "state-home"))).IsNotEqualTo(env["XDG_STATE_HOME"]);
    }

    [Test]
    public async Task No_recent_models_leaves_the_private_state_empty() {
        var run = Tmp.CreateDir("run-state").Path;

        OpenCodeCliRunner.CopyRecentModels(new Dictionary<string, string?> { ["HOME"] = Tmp.CreateDir("empty-home").Path }, "/nowhere", run, _ => { });

        await Assert.That(Directory.EnumerateFileSystemEntries(run)).IsEmpty();
    }

    [Test]
    public async Task BuildPrompt_puts_the_system_prompt_first_and_the_schema_instruction_last() {
        await Assert.That(OpenCodeCliRunner.BuildPrompt("Name it.", null, null)).IsEqualTo("Name it.");
        await Assert.That(OpenCodeCliRunner.BuildPrompt("Name it.", "  ", "  ")).IsEqualTo("Name it.");
        await Assert.That(OpenCodeCliRunner.BuildPrompt("Name it.", " You write titles. ", TitleSchema)).IsEqualTo(
            "<system_prompt>\nYou write titles.\n</system_prompt>\n\nName it.\n\n" + StructuredReply.SchemaInstruction(TitleSchema));
    }

    [Test]
    public async Task BuildEnvironment_sets_the_lockdown_over_whatever_was_inherited() {
        var config = new OpenCodeHeadlessConfig("""{"tools":{"*":false}}""", """{"*":"deny"}""", null);
        var env = OpenCodeCliRunner.BuildEnvironment(new Dictionary<string, string?> {
            ["OPENCODE_CONFIG"]         = "/user/extra.json",
            ["OPENCODE_CONFIG_DIR"]     = "/user/config",
            ["OPENCODE_CONFIG_CONTENT"] = """{"mcp":{}}""",
            ["OPENCODE_PERMISSION"]     = """{"*":"allow"}""",
            ["XDG_CONFIG_HOME"]         = "/user/xdg-config",
            ["XDG_DATA_HOME"]           = "/user/data",
            ["XDG_STATE_HOME"]          = "/user/state",
            ["XDG_CACHE_HOME"]          = "/user/cache",
            ["HOME"]                    = "/user",
            ["OPENCODE_TEST_HOME"]      = "/user/test-home",
            ["ANTHROPIC_API_KEY"]       = "sk-kept",
        }, Dirs, config, "/fallback");

        await Assert.That(env.ContainsKey("OPENCODE_CONFIG")).IsFalse();
        await Assert.That(env["HOME"]).IsEqualTo("/run/home");
        await Assert.That(env["OPENCODE_TEST_HOME"]).IsEqualTo("/run/home");
        if (OperatingSystem.IsWindows()) await Assert.That(env["USERPROFILE"]).IsEqualTo("/run/home");
        await Assert.That(env["XDG_CONFIG_HOME"]).IsEqualTo("/run/xdg-config");
        await Assert.That(env["OPENCODE_CONFIG_DIR"]).IsEqualTo("/run/config");
        await Assert.That(env["XDG_DATA_HOME"]).IsEqualTo("/run/data");
        await Assert.That(env["XDG_STATE_HOME"]).IsEqualTo("/run/state");
        await Assert.That(env["XDG_CACHE_HOME"]).IsEqualTo("/user/cache");
        await Assert.That(env["OPENCODE_CONFIG_CONTENT"]).IsEqualTo(config.ConfigContent);
        await Assert.That(env["OPENCODE_PERMISSION"]).IsEqualTo("""{"*":"deny"}""");
        foreach (var flag in new[] {
                     "OPENCODE_DISABLE_PROJECT_CONFIG", "OPENCODE_DISABLE_CLAUDE_CODE", "OPENCODE_DISABLE_EXTERNAL_SKILLS",
                     "OPENCODE_DISABLE_AUTOUPDATE", "OPENCODE_DISABLE_MODELS_FETCH", "KCAP_SKIP" })
            await Assert.That(env[flag]).IsEqualTo("1");
        await Assert.That(env["ANTHROPIC_API_KEY"]).IsEqualTo("sk-kept");
        await Assert.That(env.ContainsKey("OPENCODE_AUTH_CONTENT")).IsFalse();
    }

    /// <summary>The private home would otherwise move OpenCode's cache, and with models fetch off the model list
    /// with it.</summary>
    [Test]
    public async Task BuildEnvironment_pins_the_cache_under_the_users_own_home_when_none_is_inherited() {
        var homeVariable = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
        var config       = new OpenCodeHeadlessConfig("{}", "{}", null);

        var inherited = OpenCodeCliRunner.BuildEnvironment(new Dictionary<string, string?> { [homeVariable] = "/user" }, Dirs, config, "/fallback");
        var fallback  = OpenCodeCliRunner.BuildEnvironment(new Dictionary<string, string?>(), Dirs, config, "/fallback");

        await Assert.That(inherited["XDG_CACHE_HOME"]).IsEqualTo(Path.Combine("/user", ".cache"));
        await Assert.That(fallback["XDG_CACHE_HOME"]).IsEqualTo(Path.Combine("/fallback", ".cache"));
        await Assert.That(fallback[homeVariable]).IsEqualTo("/run/home");
    }

    [Test]
    public async Task BuildEnvironment_passes_the_users_credentials_when_there_are_some() {
        var env = OpenCodeCliRunner.BuildEnvironment(new Dictionary<string, string?>(), Dirs,
            new OpenCodeHeadlessConfig("{}", "{}", """{"anthropic":{}}"""), "/fallback");

        await Assert.That(env["OPENCODE_AUTH_CONTENT"]).IsEqualTo("""{"anthropic":{}}""");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_captured_success_is_a_result_with_its_usage_and_cost() {
        var outcome = await Run(Replaying(Fixture("success.jsonl")), model: "anthropic/claude-haiku-4-5");

        await Assert.That(outcome.Failure).IsNull();
        var result = outcome.Result!;
        await Assert.That(result.Result).IsEqualTo("""{"verdict":"fail","reason":"The function starts iterating from index 1 instead of 0, causing it to skip the first element of the list."}""");
        await Assert.That(result.Model).IsEqualTo("anthropic/claude-haiku-4-5");
        await Assert.That(result.InputTokens + result.OutputTokens + result.CacheReadTokens + result.CacheWriteTokens).IsEqualTo(16_748);
        await Assert.That(Math.Round(result.CostUsd!.Value, 4)).IsEqualTo(0.0123);
        await RunsRootIsEmpty();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_captured_rejected_key_is_not_signed_in() {
        var outcome = await Run(Replaying(Fixture("unauthorised.jsonl"), exitCode: 1));

        await Assert.That(outcome.Result).IsNull();
        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.NotSignedIn);
        await Assert.That(outcome.Error).IsEqualTo("invalid x-api-key");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task An_error_event_with_a_spend_status_is_spend_budget() {
        var outcome = await Run(ReplayingLines("""{"type":"error","error":{"name":"APIError","data":{"message":"Too many","statusCode":429}}}"""));

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.SpendBudget);
    }

    /// <summary>OpenCode emits no event on a usage limit and retries until killed: only the log line ends the run
    /// before the deadline.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task A_usage_limit_logged_on_stderr_kills_the_run_at_once_as_spend_budget() {
        var log = Tmp.CreateFile("limit.log", File.ReadAllText(Fixture("usage-limit.stderr.log")));
        var opencode = FakeOpenCode($"echo '{StepFinish}'\ncat '{log}' >&2\nexec sleep 30");
        var started  = Environment.TickCount64;

        var outcome = await Run(opencode, timeout: TimeSpan.FromSeconds(120));

        await Assert.That(Environment.TickCount64 - started).IsLessThan(60_000);
        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.SpendBudget);
        await Assert.That(outcome.Error!).Contains("Free usage limit reached");
        await Assert.That(outcome.Usage).IsEqualTo(new ClaudeCliUsage(10, 2, 3, 1, 0.0005));
        await RunsRootIsEmpty();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task An_exit_with_no_events_is_classified_from_stderr() {
        var outcome = await Run(FakeOpenCode("echo 'Error: No API key found for provider anthropic' >&2\nexit 1"));

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.NotSignedIn);
        await Assert.That(outcome.Error!).Contains("No API key");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_failed_exit_with_a_reply_is_still_a_failure() {
        var outcome = await Run(FakeOpenCode($"echo '{Reply("half")}'\nexit 2"));

        await Assert.That(outcome.Result).IsNull();
        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.ProcessFailure);
    }

    [Test, ExcludeOn(OS.Windows)]
    [Arguments("```json\n{\"title\": \"hello\"}\n```", """{"title":"hello"}""")]
    [Arguments("{\"title\":\"hello\",\"extra\":1}", null)]
    [Arguments("The title is hello.", null)]
    public async Task A_schema_reply_is_returned_reserialised_only_when_it_matches(string reply, string? expected) {
        var outcome = await Run(ReplayingLines(Reply(reply), StepFinish), jsonSchema: TitleSchema);

        if (expected is null) {
            await Assert.That(outcome.Result).IsNull();
            await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.OutputUnparseable);
            await Assert.That(outcome.Usage).IsNotNull();
        } else {
            await Assert.That(outcome.Result!.Result).IsEqualTo(expected);
        }
    }

    /// <summary>A stream cut off mid-step exits 0 having written only part of the step's text.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task Text_with_no_step_finish_after_it_is_a_process_failure_keeping_the_usage() {
        var outcome = await Run(ReplayingLines(StepFinish, """{"type":"step_start","part":{}}""", Reply("half an ans")));

        await Assert.That(outcome.Result).IsNull();
        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.ProcessFailure);
        await Assert.That(outcome.Usage).IsNotNull();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_run_with_no_text_is_unparseable() {
        var outcome = await Run(ReplayingLines(StepFinish));

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.OutputUnparseable);
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Without_a_requested_model_none_is_reported() {
        var outcome = await Run(ReplayingLines(Reply("ok"), StepFinish));

        await Assert.That(outcome.Result!.Model).IsNull();
    }

    [Test, ExcludeOn(OS.Windows)]
    [Arguments("""{"type":"object","patternProperties":{"^x":{"type":"string"}}}""", "patternProperties")]
    [Arguments("""{"type":""", "JSON")]
    public async Task A_schema_the_reply_cannot_be_checked_against_refuses_before_opencode_starts(string schema, string named) {
        var marker  = Path.Combine(Tmp.Path, "started");
        var outcome = await Run(FakeOpenCode($"touch '{marker}'"), jsonSchema: schema);

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.ProcessFailure);
        await Assert.That(outcome.Error!).Contains(named);
        await Assert.That(File.Exists(marker)).IsFalse();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task An_unreadable_user_config_refuses_before_opencode_starts() {
        var marker = Path.Combine(Tmp.Path, "started");
        var env    = DaemonEnvironment();
        env["OPENCODE_CONFIG_CONTENT"] = "{broken";

        var outcome = await Run(FakeOpenCode($"touch '{marker}'"), env: env);

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.ProcessFailure);
        await Assert.That(outcome.Error!).Contains("OPENCODE_CONFIG_CONTENT");
        await Assert.That(File.Exists(marker)).IsFalse();
        await RunsRootIsEmpty();
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task A_run_root_that_is_not_private_refuses_before_opencode_starts() {
        Directory.CreateDirectory(RunsRoot);
        File.SetUnixFileMode(RunsRoot, (UnixFileMode)0b111_111_111);
        var marker = Path.Combine(Tmp.Path, "started");

        var outcome = await Run(FakeOpenCode($"touch '{marker}'"));

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.ProcessFailure);
        await Assert.That(outcome.Error!).Contains("owner-only");
        await Assert.That(File.Exists(marker)).IsFalse();
    }

    /// <summary>The script records what it was given, then answers, so the directories it saw can be checked for
    /// removal afterwards. The inherited environment carries an operator's allow-all permission, an extra config
    /// file and no <c>auth.json</c> on the user's side.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task OpenCode_gets_the_prompt_on_utf8_stdin_in_an_empty_private_run() {
        var seen = Tmp.CreateDir("seen").Path;
        var opencode = FakeOpenCode($"""
            printf '%s\n' "$@" > '{seen}/args'
            pwd > '{seen}/cwd'
            ls -A > '{seen}/listing'
            ls -A "$HOME" "$XDG_CONFIG_HOME" "$OPENCODE_CONFIG_DIR" "$XDG_DATA_HOME" "$XDG_STATE_HOME" > '{seen}/private-listing'
            printf '%s\n' "$HOME" "$OPENCODE_TEST_HOME" "$XDG_CONFIG_HOME" "$OPENCODE_CONFIG_DIR" "$XDG_DATA_HOME" "$XDG_STATE_HOME" > '{seen}/private-dirs'
            printf '%s' "$OPENCODE_PERMISSION" > '{seen}/permission'
            env | grep -c '^OPENCODE_CONFIG=' > '{seen}/config'
            env | grep -c '^OPENCODE_AUTH_CONTENT=' > '{seen}/auth'
            printf '%s' "$KCAP_SKIP" > '{seen}/skip'
            cat > '{seen}/prompt'
            echo '{Reply("ok")}'
            echo '{StepFinish}'
            """);
        var env = DaemonEnvironment();
        env["OPENCODE_PERMISSION"] = """{"*":"allow"}""";
        env["OPENCODE_CONFIG"]     = Tmp.CreateFile("extra.json", "{}");
        const string prompt = "Grüße, 世界 — naïve café ✓";

        var outcome = await Run(opencode, prompt, systemPrompt: "Be brief.", jsonSchema: null, env: env);

        await Assert.That(outcome.Result!.Result).IsEqualTo("ok");
        var args       = await File.ReadAllLinesAsync(Path.Combine(seen, "args"));
        var cwd        = Path.Combine(Path.GetDirectoryName((await File.ReadAllLinesAsync(Path.Combine(seen, "private-dirs")))[0])!, "cwd");
        await Assert.That(args).IsEquivalentTo(OpenCodeCliRunner.BuildArgs(null), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Path.GetFileName((await File.ReadAllTextAsync(Path.Combine(seen, "cwd"))).Trim())).IsEqualTo("cwd");
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(seen, "prompt"))).IsEquivalentTo(
            new UTF8Encoding(false).GetBytes(OpenCodeCliRunner.BuildPrompt(prompt, "Be brief.", null)), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That((await File.ReadAllTextAsync(Path.Combine(seen, "listing"))).Trim()).IsEmpty();
        await Assert.That((await File.ReadAllLinesAsync(Path.Combine(seen, "private-listing"))).Where(l => l.Length > 0 && !l.EndsWith(':')))
            .IsEmpty();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(seen, "permission"))).IsEqualTo(
            OpenCodeHeadlessConfig.Build(Paths, env).Permission);
        await Assert.That((await File.ReadAllTextAsync(Path.Combine(seen, "config"))).Trim()).IsEqualTo("0");
        await Assert.That((await File.ReadAllTextAsync(Path.Combine(seen, "auth"))).Trim()).IsEqualTo("0");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(seen, "skip"))).IsEqualTo("1");

        var run = Path.GetDirectoryName(cwd)!;
        await Assert.That(Path.GetFileName(cwd)).IsEqualTo("cwd");
        await Assert.That(Path.GetDirectoryName(run)).IsEqualTo(RunsRoot);
        await Assert.That(Path.GetFileName(run)).StartsWith("opencode-test-");
        await Assert.That(await File.ReadAllLinesAsync(Path.Combine(seen, "private-dirs"))).IsEquivalentTo(
            new[] { "home", "home", "xdg-config", "config", "data", "state" }.Select(name => Path.Combine(run, name)), TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Directory.Exists(run)).IsFalse();
        await RunsRootIsEmpty();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task The_users_credentials_reach_opencode_only_through_the_environment() {
        const string auth = """{"anthropic":{"type":"api","key":"sk-test"}}""";
        Tmp.CreateFile(Path.Combine("user-data", "opencode", "auth.json"), auth);
        var seen     = Path.Combine(Tmp.Path, "auth");
        var opencode = FakeOpenCode($"printf '%s' \"$OPENCODE_AUTH_CONTENT\" > '{seen}'\necho '{Reply("ok")}'\necho '{StepFinish}'");

        var outcome = await Run(opencode);

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(await File.ReadAllTextAsync(seen)).IsEqualTo(auth);
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task The_deadline_kills_opencode_keeps_the_usage_reported_so_far_and_removes_every_directory() {
        var opencode = FakeOpenCode($"echo '{StepFinish}'\nexec sleep 30");

        var outcome = await Run(opencode, timeout: TimeSpan.FromSeconds(1));

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.Timeout);
        await Assert.That(outcome.Usage!.InputTokens).IsEqualTo(10);
        await RunsRootIsEmpty();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Cancellation_kills_opencode_and_removes_every_directory_before_it_propagates() {
        var started  = Path.Combine(Tmp.Path, "started");
        var opencode = FakeOpenCode($"touch '{started}'\nexec sleep 30");
        using var cts = new CancellationTokenSource();

        var run    = Run(opencode, ct: cts.Token);
        var waited = Environment.TickCount64;
        while (!File.Exists(started) && Environment.TickCount64 - waited < 10_000) await Task.Delay(20);
        await cts.CancelAsync();

        await Assert.That(async () => await run).Throws<OperationCanceledException>();
        await RunsRootIsEmpty();
    }
}
