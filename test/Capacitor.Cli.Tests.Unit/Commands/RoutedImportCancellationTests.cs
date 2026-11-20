using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.MistralVibe;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Harness.Copilot;
using Capacitor.Cli.Harness.Cursor;
using Capacitor.Cli.Harness.Gemini;
using Capacitor.Cli.Harness.Grok;
using Capacitor.Cli.Harness.Kiro;
using Capacitor.Cli.Harness.MistralVibe;
using Capacitor.Cli.Harness.Muse;
using Capacitor.Cli.Harness.Pi;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A cancel landing during a routed source's session-start post ends its import as cancelled,
/// not as a failed session.</summary>
public class RoutedImportCancellationTests {
    [TempDir]        public required TempDir        Tmp    { get; init; }
    [TempHome]       public required TempHome       Home   { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static async Task AssertCancelledAsync(IImportSource source, ImportCommand.SessionClassification classification) {
        using var cts    = new CancellationTokenSource();
        using var client = new HttpClient(new CancelOnSessionStartHandler(cts));
        var ctx = new ImportContext(client, "http://kcap.invalid", ForcePrivate: false);

        await Assert.That(async () => await source.ImportSessionAsync(classification, ctx, cts.Token))
            .Throws<OperationCanceledException>();
    }

    static ImportCommand.SessionClassification Classification(HarnessId vendor, Dictionary<string, object?> sourceMeta) => new() {
        SessionId  = "cancelled-1",
        FilePath   = "",
        EncodedCwd = "",
        Meta       = new SessionMetadata(),
        Status     = ImportCommand.ClassificationStatus.New,
        SourceMeta = sourceMeta,
        Vendor     = vendor,
    };

    string Transcript(string name = "transcript.jsonl") =>
        Tmp.CreateFile(name, """{"role":"user","content":"hi"}""" + "\n");

    [Test]
    public Task Copilot() => AssertCancelledAsync(
        new CopilotImportSource(Config.Root, CopilotHarness.FromEnvironment(Home).Paths, [], router: new GitProviderRouter(), time: TimeProvider.System),
        Classification(HarnessId.Copilot, new() { ["TranscriptPath"] = Transcript() }));

    [Test]
    public Task Gemini() => AssertCancelledAsync(
        new GeminiImportSource(GeminiHarness.FromEnvironment(Home).Paths.TmpDir, TimeProvider.System),
        Classification(HarnessId.Gemini, new() { ["TranscriptPath"] = Transcript() }));

    [Test]
    public Task Kiro() => AssertCancelledAsync(
        new KiroImportSource(Config.Root, KiroHarness.FromEnvironment(Home).Paths.SessionsDir, KiroHarness.FromEnvironment(Home).Crew, router: new GitProviderRouter(), time: TimeProvider.System),
        Classification(HarnessId.Kiro, new() { ["TranscriptPath"] = Transcript() }));

    [Test]
    public Task Pi() => AssertCancelledAsync(
        new PiImportSource(Config.Root, PiHarness.FromEnvironment(Home).Paths.SessionsDir, router: new GitProviderRouter(), time: TimeProvider.System),
        Classification(HarnessId.Pi, new() { ["TranscriptPath"] = Transcript() }));

    [Test]
    public Task Cursor() => AssertCancelledAsync(
        new CursorImportSource(Config.Root, Tmp.PathTo("cursor-projects"), Tmp.PathTo("cursor-workspace-storage"), router: new GitProviderRouter(), time: TimeProvider.System),
        Classification(HarnessId.Cursor, new() { ["TranscriptPath"] = Transcript(), ["WorkspaceFolder"] = Tmp.Path }));

    [Test]
    public Task Muse() => AssertCancelledAsync(
        new MuseImportSource(new MusePaths(new UserHome(Home.Path), null, Tmp.PathTo("muse-data")), new WatcherPaths(Tmp.PathTo("watchers")), TimeProvider.System),
        Classification(HarnessId.Muse, new() { ["LogPath"] = Transcript() }));

    [Test]
    public Task MistralVibe() {
        Transcript(Path.Combine("vibe", "messages.jsonl"));
        return AssertCancelledAsync(
            new MistralVibeImportSource(new MistralVibePaths(new UserHome(Home.Path), Tmp.PathTo("vibe-home")), TimeProvider.System),
            Classification(HarnessId.MistralVibe, new() { ["Dir"] = Tmp.PathTo("vibe"), ["Format"] = "legacy" }));
    }

    [Test]
    public Task Grok() {
        Transcript(Path.Combine("grok", "updates.jsonl"));
        return AssertCancelledAsync(
            new GrokImportSource(new GrokPaths(new UserHome(Home.Path), null), _ => Task.FromResult<RepositoryPayload?>(null), TimeProvider.System),
            Classification(HarnessId.Grok, new() { ["Dir"] = Tmp.PathTo("grok") }));
    }
}
