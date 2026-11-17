using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpCaptureScopeTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    McpCaptureScope Scope(Profile profile) =>
        new(Config.Root, Resolutions.Of(profile), new GitProviderRouter(), Home, new WorkingDirectory(AppContext.BaseDirectory), TimeProvider.System);

    [Test]
    public async Task A_project_under_an_excluded_path_is_out_of_scope() {
        var excluded = Tmp.CreateDir("excl");

        await Assert.That(await Scope(new Profile { ExcludedPaths = [excluded] }).IsOutOfScopeAsync(excluded.PathTo("project"))).IsTrue();
    }

    [Test]
    public async Task A_project_outside_the_path_allowlist_is_out_of_scope() {
        var allowed = Tmp.CreateDir("allowed");
        var other   = Tmp.CreateDir("other");

        await Assert.That(await Scope(new Profile { AllowedPaths = [allowed] }).IsOutOfScopeAsync(other.PathTo("project"))).IsTrue();
        await Assert.That(await Scope(new Profile { AllowedPaths = [allowed] }).IsOutOfScopeAsync(allowed.PathTo("project"))).IsFalse();
    }

    /// <summary>The hook's persisted verdict wins over a fresh probe, which could resolve a repo the hook's budget did not.</summary>
    [Test]
    public async Task A_session_the_hook_excluded_is_out_of_scope_whatever_the_profile_says() {
        const string sessionId = "0f3c2a4e-1111-4222-8333-944455556666";
        DisabledSessions.Exclude(sessionId, Config.Root);

        await Assert.That(await Scope(new Profile()).IsOutOfScopeAsync(Tmp.CreateDir("anything"), sessionId)).IsTrue();
        await Assert.That(await Scope(new Profile()).IsOutOfScopeAsync(Tmp.CreateDir("other"), "another-session")).IsFalse();
    }

    [Test]
    public async Task The_Claude_spelling_is_consulted_only_for_the_session_it_names() {
        const string claude = "0f3c2a4e-1111-4222-8333-944455556666";
        var canonical = SessionIds.Canonical(claude)!;

        await Assert.That(McpCaptureScope.MarkerIds(canonical, claude)).IsEquivalentTo(new[] { canonical, claude });
        await Assert.That(McpCaptureScope.MarkerIds("codex-thread", claude)).IsEquivalentTo(new[] { "codex-thread" });
        await Assert.That(McpCaptureScope.MarkerIds(null, claude)).IsEmpty();
    }

    [Test]
    public async Task A_profile_with_no_scope_lists_keeps_every_project_in_scope() =>
        await Assert.That(await Scope(new Profile()).IsOutOfScopeAsync(Tmp.CreateDir("anything"))).IsFalse();
}
