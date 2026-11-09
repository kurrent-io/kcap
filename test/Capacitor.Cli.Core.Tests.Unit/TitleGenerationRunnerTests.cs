using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Setup;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>Which vendor CLI titles a session. Fake <c>claude</c> and <c>pi</c> scripts each answer with their own
/// title, so the title returned names the runner that produced it.</summary>
public class TitleGenerationRunnerTests {
    [TempDir]        public required TempDir        Tmp    { get; init; }
    [TempHome]       public required TempHome       Home   { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ClaudeTitle = "Claude title";
    const string PiTitle     = "Pi title";

    TempDirHandle? _bin;

    TempDirHandle Bin => _bin ??= Tmp.CreateDir("bin");

    void FakeClaude() =>
        Bin.CreateExecutable("claude", $"#!/bin/sh\necho '{{\"result\":\"{ClaudeTitle}\"}}'\n");

    void FakePi(string body) => Bin.CreateExecutable("pi", $"#!/bin/sh\n{body}\n");

    void AnsweringPi() {
        var reply = new JsonObject {
            ["type"] = "agent_end",
            ["messages"] = new JsonArray(new JsonObject {
                ["role"]       = "assistant",
                ["content"]    = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = PiTitle }),
                ["stopReason"] = "stop",
            }),
        }.ToJsonString();

        FakePi($"cat > /dev/null\necho '{reply}'");
    }

    Task<ClaudeCliResult?> Generate(string vendor, TimeProvider? time = null) =>
        TitleGeneration.GenerateAsync("Fix the login timeout", "Done.", time ?? TimeProvider.System, _ => { }, null,
            TestHarnesses.Under(Home.Home, BinaryProbe.Searching(Bin.Path)), Config.Root, vendor);

    static async Task Started(string marker) {
        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!File.Exists(marker)) {
            if (DateTime.UtcNow > giveUp) throw new TimeoutException($"'{marker}' never appeared");
            await Task.Delay(20);
        }
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_pi_session_is_titled_by_pi() {
        FakeClaude();
        AnsweringPi();

        await Assert.That((await Generate("pi"))?.Result).IsEqualTo(PiTitle);
    }

    [Test, ExcludeOn(OS.Windows)]
    [Arguments("missing")]
    [Arguments("failing")]
    [Arguments("refusing")]
    public async Task A_pi_session_falls_back_to_claude_when_pi_gives_no_title(string pi) {
        FakeClaude();

        switch (pi) {
            case "failing":
                FakePi("cat > /dev/null\necho 'No API key found' >&2\nexit 1");
                break;
            case "refusing":
                FakePi("cat > /dev/null\necho '{\"type\":\"agent_end\",\"messages\":[{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"I cannot help with that\"}],\"stopReason\":\"stop\"}]}'");
                break;
        }

        await Assert.That((await Generate("pi"))?.Result).IsEqualTo(ClaudeTitle);
    }

    /// <summary>A watcher shutting down waits <see cref="TitleGeneration.Budget"/> for a title. A pi that hangs past its
    /// allowance is killed and claude starts with less than its own timeout left: the deadline, not claude's timeout,
    /// ends it.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task The_claude_fallback_ends_at_the_budget_not_at_its_own_timeout() {
        var clock         = new FakeTimeProvider();
        var piStarted     = Tmp.PathTo("pi-started");
        var claudeStarted = Tmp.PathTo("claude-started");
        FakePi($"cat > /dev/null\ntouch '{piStarted}'\nsleep 120");
        Bin.CreateExecutable("claude", $"#!/bin/sh\ntouch '{claudeStarted}'\nsleep 120\n");

        var title = Generate("pi", clock);
        await Started(piStarted);
        clock.Advance(TitleGeneration.Budget - TimeSpan.FromSeconds(5));
        await Started(claudeStarted);
        clock.Advance(TimeSpan.FromSeconds(5));

        await Assert.That(await title.WaitAsync(TimeSpan.FromSeconds(10))).IsNull();
    }

    [Test, ExcludeOn(OS.Windows)]
    [Arguments("claude")]
    [Arguments("opencode")]
    [Arguments("gemini")]
    public async Task Other_vendors_are_titled_by_claude_even_with_pi_installed(string vendor) {
        FakeClaude();
        AnsweringPi();

        await Assert.That((await Generate(vendor))?.Result).IsEqualTo(ClaudeTitle);
    }
}
