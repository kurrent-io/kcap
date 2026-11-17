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

    [Test]
    public async Task A_profile_with_no_scope_lists_keeps_every_project_in_scope() =>
        await Assert.That(await Scope(new Profile()).IsOutOfScopeAsync(Tmp.CreateDir("anything"))).IsFalse();
}
