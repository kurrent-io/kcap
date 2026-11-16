using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class DetachedBacklogImportTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    const string Session   = "9dc2775376454e4691ecc2d69973c152";
    const string ServerUrl = "https://hook.example";

    /// <summary>The continuation outlives a hook that has to answer on its stdout: it names its vendor and session, which
    /// stands in for a scope and a confirmation, sends whatever the session's length, reads the hook's config root and server, runs as a detached import (no parent
    /// watchdog, output in a log) and holds none of the hook's standard streams.</summary>
    [Test]
    public async Task The_import_runs_detached_on_the_hooks_config_holding_none_of_its_streams() {
        var starter = FakeProcessStarter.Refusing();

        DetachedBacklogImport.Spawn("--pi", Session, Tmp.PathTo("sessions", "s.jsonl"), Tmp.PathTo("sessions"), Config.Root, ServerUrl, starter);

        var psi = starter.Seen!;
        await Assert.That(psi.ArgumentList).IsEquivalentTo(
            ["import", "--pi", "--session", Session, "--min-lines", "0", "--skip-title", "--no-update-check"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(psi.Environment[Capacitor.Cli.Core.ConfigRoot.ConfigDirEnvVar]).IsEqualTo(Config.Root.Directory);
        await Assert.That(psi.Environment[Capacitor.Cli.Core.Config.ProfileOverrides.UrlVar]).IsEqualTo(ServerUrl);
        await Assert.That(psi.RedirectStandardInput && psi.RedirectStandardOutput && psi.RedirectStandardError).IsTrue();
        await Assert.That(File.Exists(psi.Environment[DetachedImportLog.EnvVar])).IsTrue();
    }

    /// <summary>A transcript the vendor's import would not look for is not handed over: the import would find nothing,
    /// and the hook would claim a recovery that never happens.</summary>
    [Test]
    public async Task A_transcript_outside_where_the_import_looks_is_not_handed_over() {
        var starter = FakeProcessStarter.Refusing();

        var handed = DetachedBacklogImport.Spawn("--pi", Session, Tmp.PathTo("elsewhere", "s.jsonl"), Tmp.PathTo("sessions"), Config.Root, ServerUrl, starter);

        await Assert.That(handed).IsFalse();
        await Assert.That(starter.Starts).IsEqualTo(0);
    }

    [Test]
    public async Task A_session_the_person_disabled_is_not_handed_over() {
        Capacitor.Cli.Core.DisabledSessions.Mark(Session, Config.Root);
        var starter = FakeProcessStarter.Refusing();

        var handed = DetachedBacklogImport.Spawn("--copilot", Session, Tmp.PathTo("s.jsonl"), null, Config.Root, ServerUrl, starter);

        await Assert.That(handed).IsFalse();
        await Assert.That(starter.Starts).IsEqualTo(0);
    }
}
