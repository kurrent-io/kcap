using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Recording.PrDetection;
using Spectre.Console;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// An import that cannot ask — <c>--yes</c> at a terminal, or no terminal at all — keeps every capture
/// exclusion and never reads stdin for one.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class ImportExclusionTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    const string Secret = "secret-sess";
    const string Open   = "open-sess";

    /// <summary>One session under the excluded directory, one beside it.</summary>
    (string Projects, string Excluded) Fixture() {
        var excluded = Tmp.CreateDir("secret-project");
        var open     = Tmp.CreateDir("open-project");
        var projects = Tmp.CreateDir("projects");

        WriteSession(projects.CreateDir("-secret-project"), Secret, excluded);
        WriteSession(projects.CreateDir("-open-project"), Open, open);

        return (projects.Path, excluded.Path);
    }

    static void WriteSession(TempDirHandle dir, string sessionId, string cwd) {
        // Serialized so a Windows path's backslashes are escaped and the transcript still names its cwd.
        var cwdJson = JsonSerializer.Serialize(cwd);

        dir.CreateFile($"{sessionId}.jsonl", [
            .. Enumerable.Range(0, 20).Select(i =>
                "{\"type\":\"user\",\"timestamp\":\"2026-03-15T10:00:00Z\",\"cwd\":" + cwdJson
              + ",\"message\":{\"content\":\"line-" + i + "\"}}")
        ]);
    }

    // An accepted persistent-404 session is probed at classification, ledger recording, then final confirmation.
    static WireMockServer Online(int secretStatus = 404) {
        var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath($"/api/sessions/{Open}/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        var secretResponse = Response.Create().WithStatusCode(secretStatus);
        if (secretStatus == 200) secretResponse.WithBody("{\"last_line_number\":4}");
        server.Given(Request.Create().WithPath($"/api/sessions/{Secret}/last-line").UsingGet())
            .RespondWith(secretResponse);
        foreach (var path in new[] { "/hooks/transcript", "/hooks/session-start*", "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/set-title", "/hooks/session-title" })
            server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        return server;
    }

    async Task<(int Exit, ImportCommand.ImportRunOutcome? Outcome)> RunAsync(
            WireMockServer server, string projects, string excluded, ImportConsole console,
            bool skipConfirmation = true, string[]? exclusions = null, string[]? excludedRepos = null) {
        ImportCommand.ImportRunOutcome? outcome = null;

        var import = new ImportCommand(Config.Root,
            Resolutions.Of(new Core.Config.Profile { ExcludedPaths = exclusions ?? [excluded], ExcludedRepos = excludedRepos ?? [] }, "exclusion-test", server.Url!), Home,
            TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), new GitProviderRouter(), TimeProvider.System, console: console);

        var exit = await import.HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projects, new GitProviderRouter(), TimeProvider.System)],
            scope: new ImportScope.All(),
            skipConfirmation: skipConfirmation,
            skipTitle: true,
            onFinished: o => outcome = o);

        return (exit, outcome);
    }

    [Test]
    public async Task Yes_at_a_terminal_keeps_the_exclusion_without_reading_stdin() {
        // The terminal case: stdin and stdout are both a TTY, so only --yes stands between the run and
        // a prompt. A "y" waiting on stdin would include the session if anything read it.
        var (projects, excluded) = Fixture();
        using var server = Online();

        var stdin = new StringReader("y\ny\ny\n");
        var error = new StringWriter { NewLine = "\n" };
        var ansi  = AnsiConsole.Create(new AnsiConsoleSettings {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out         = new AnsiConsoleOutput(new StringWriter()),
        });
        ansi.Profile.Width = 120;

        var console = new ImportConsole(new StringWriter(), error, stdin, ansi, OutputRedirected: false, InputRedirected: false);

        var (exit, outcome) = await RunAsync(server, projects, excluded, console);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdin.Peek()).IsEqualTo((int)'y');
        await Assert.That(error.ToString()).DoesNotContain("is excluded. Include");
        await Assert.That(error.ToString()).Contains("Auto-skipping 1 session(s) from 1 excluded source(s) (--yes keeps exclusions).");
        await Assert.That(outcome!.Counts.Excluded).IsEqualTo(1);
        await Assert.That(outcome.Counts.Imported).IsEqualTo(1);
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path == $"/api/sessions/{Secret}/last-line")).IsFalse();
    }

    [Test]
    public async Task Redirected_stdin_keeps_the_exclusion() {
        var (projects, excluded) = Fixture();
        using var server = Online();
        var terminal = new TestImportTerminal();

        var (exit, outcome) = await RunAsync(server, projects, excluded, terminal.Console);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(terminal.Error.ToString()).Contains("Auto-skipping 1 session(s)");
        await Assert.That(outcome!.Counts.Excluded).IsEqualTo(1);
        await Assert.That(outcome.Counts.Imported).IsEqualTo(1);
        await Assert.That(outcome.Counts.Failed).IsEqualTo(0);
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path == $"/api/sessions/{Secret}/last-line")).IsFalse();
    }

    [Test]
    [NotInParallel]
    [Arguments("y", 404, 2, 0, 3, 0, 0)]
    [Arguments("n", 404, 1, 1, 0, 0, 0)]
    [Arguments("y", 200, 2, 0, 2, 1, 0)]
    [Arguments("y", 204, 1, 0, 1, 0, 1)]
    public async Task An_excluded_session_is_probed_only_after_its_terminal_opt_in(
            string answer, int secretStatus, int imported, int excludedCount, int probes, int resumed, int alreadyLoaded) {
        var (projects, excluded) = Fixture();
        using var server = Online(secretStatus);
        using var ansi = new ScriptedConsole().Line("y");
        var probesBeforeAnswer = -1;
        using var stdin = new ObservedReader(answer + "\n", () =>
            probesBeforeAnswer = server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/sessions/{Secret}/last-line"));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var console = new ImportConsole(output, error, stdin, ansi, OutputRedirected: false, InputRedirected: false);

        var (exit, outcome) = await RunAsync(server, projects, excluded, console, skipConfirmation: false);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(probesBeforeAnswer).IsEqualTo(0);
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/sessions/{Secret}/last-line")).IsEqualTo(probes);
        await Assert.That(outcome!.Counts.Imported).IsEqualTo(imported);
        await Assert.That(outcome.Counts.Excluded).IsEqualTo(excludedCount);
        await Assert.That(outcome.Counts.Resumed).IsEqualTo(resumed);
        await Assert.That(outcome.Counts.AlreadyLoaded).IsEqualTo(alreadyLoaded);
        await Assert.That(outcome.Counts.Failed).IsEqualTo(0);

        var sentLines = server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .SelectMany(e => {
                using var doc = JsonDocument.Parse(e.RequestMessage.Body!);
                return doc.RootElement.GetProperty("session_id").GetString() == Secret
                    ? doc.RootElement.GetProperty("line_numbers").EnumerateArray().Select(n => n.GetInt32()).ToArray()
                    : Array.Empty<int>();
            }).ToArray();
        if (secretStatus == 200) await Assert.That(sentLines).IsEquivalentTo(Enumerable.Range(5, 15));
        if (secretStatus == 204 || answer == "n") await Assert.That(sentLines).IsEmpty();
    }

    sealed class ObservedReader(string text, Action onRead) : StringReader(text) {
        public override string? ReadLine() {
            onRead();
            return base.ReadLine();
        }
    }

    [Test]
    [NotInParallel]
    public async Task Opting_in_one_path_does_not_release_a_transcript_that_moves_to_another_excluded_path() {
        var (projects, excluded) = Fixture();
        var otherExcluded = Tmp.CreateDir("another-secret-project").Path;
        using var server = Online();
        using var ansi = new ScriptedConsole().Line("y");
        using var stdin = new ObservedReader("y\n", () =>
            WriteSession(Tmp.CreateDir("projects").CreateDir("-secret-project"), Secret, otherExcluded));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var console = new ImportConsole(output, error, stdin, ansi, OutputRedirected: false, InputRedirected: false);

        var (exit, outcome) = await RunAsync(server, projects, excluded, console,
            skipConfirmation: false, exclusions: [excluded, otherExcluded]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path == $"/api/sessions/{Secret}/last-line")).IsFalse();
        await Assert.That(server.LogEntries.Where(e => e.RequestMessage.Method == "POST")
            .Any(e => e.RequestMessage.Body?.Contains(Secret, StringComparison.Ordinal) == true)).IsFalse();
        await Assert.That(outcome!.Counts.Excluded).IsEqualTo(1);
        await Assert.That(outcome.Counts.Imported).IsEqualTo(1);
        await Assert.That(outcome.Counts.Failed).IsEqualTo(0);
    }

    [Test]
    [NotInParallel]
    [Arguments("y\nn\n", 1, 1, 0)]
    [Arguments("n\ny\n", 1, 1, 0)]
    [Arguments("y\ny\n", 2, 0, 3)]
    public async Task A_probe_waits_until_both_repository_and_path_exclusions_are_opted_in(
            string answers, int imported, int excludedCount, int probes) {
        var (projects, excluded) = Fixture();
        using var repo = GitRepo.InitIn(new TempDirHandle(excluded));
        repo.AddRemote("https://github.com/acme/secret.git");
        using var server = Online();
        using var ansi = new ScriptedConsole().Line("y");
        var reads = 0;
        var probesBeforeAnswers = new List<int>();
        using var stdin = new ObservedReader(answers, () => {
            reads++;
            probesBeforeAnswers.Add(server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/sessions/{Secret}/last-line"));
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        var console = new ImportConsole(output, error, stdin, ansi, OutputRedirected: false, InputRedirected: false);

        var (exit, outcome) = await RunAsync(server, projects, excluded, console,
            skipConfirmation: false, excludedRepos: ["acme/secret"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(reads).IsEqualTo(2);
        await Assert.That(probesBeforeAnswers.All(n => n == 0)).IsTrue();
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/sessions/{Secret}/last-line")).IsEqualTo(probes);
        await Assert.That(outcome!.Counts.Imported).IsEqualTo(imported);
        await Assert.That(outcome.Counts.Excluded).IsEqualTo(excludedCount);
        await Assert.That(outcome.Counts.Failed).IsEqualTo(0);
    }

    [Test]
    [Arguments(true, false, false, false, true)]
    [Arguments(true, false, false, true, false)]
    [Arguments(true, true, false, false, false)]
    [Arguments(false, false, false, false, false)]
    [Arguments(true, false, true, false, false)]
    public async Task Only_a_terminal_run_without_yes_may_ask_to_include_an_exclusion(
            bool tty, bool inputRedirected, bool autoSkipExclusions, bool skipConfirmation, bool expected) =>
        await Assert.That(ImportCommand.CanPromptForExclusions(tty, inputRedirected, autoSkipExclusions, skipConfirmation))
            .IsEqualTo(expected);

    [Test]
    public async Task The_yes_notice_names_how_to_include_an_exclusion_and_setup_s_does_not() {
        await Assert.That(ImportCommand.AutoSkipNotice(2, 1, skipConfirmation: true, autoSkipExclusions: false))
            .IsEqualTo("Auto-skipping 2 session(s) from 1 excluded source(s) (--yes keeps exclusions). "
                     + "To include them, run without --yes at a terminal and answer its prompt.");
        await Assert.That(ImportCommand.AutoSkipNotice(2, 1, skipConfirmation: true, autoSkipExclusions: true))
            .IsEqualTo("Auto-skipping 2 session(s) from 1 excluded source(s) (non-interactive).");
    }
}
