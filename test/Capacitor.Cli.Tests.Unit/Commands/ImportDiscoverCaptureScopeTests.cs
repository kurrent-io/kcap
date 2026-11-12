using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Harness.Copilot;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary><c>kcap import --discover</c> reports a folder <c>kcap ignore</c> covers as left out, never as a folder
/// to import or remap.</summary>
public class ImportDiscoverCaptureScopeTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    void WriteClaudeSession(string sessionId, string? cwd, string? projectDir = null) =>
        Home.CreateDir(".claude", "projects", projectDir ?? "-discover-" + sessionId[..8]).CreateFile($"{sessionId}.jsonl", [
            .. Enumerable.Range(0, 5).Select(i => {
                var line = new JsonObject {
                    ["type"]      = "user",
                    ["sessionId"] = sessionId,
                    ["timestamp"] = $"2026-03-15T10:00:0{i}Z",
                    ["message"]   = new JsonObject { ["content"] = $"step {i}" },
                };
                if (cwd is not null) line["cwd"] = cwd;

                return line.ToJsonString();
            }),
        ]);

    /// <summary>A Copilot session whose harness recorded <paramref name="repository"/> as well as its cwd.</summary>
    IImportSource WriteCopilotSession(string sessionId, string cwd, string repository) {
        var session = Tmp.CreateDir("copilot", "session-state", sessionId);
        session.CreateFile("workspace.yaml",
            $"id: {sessionId}\ncwd: '{cwd.Replace("'", "''")}'\ncreated_at: 2026-10-13T10:00:00Z\nrepository: {repository}\n");
        session.CreateFile("events.jsonl",
            """{"type":"user.message","timestamp":"2026-10-13T10:00:00Z","data":{"content":"hello"}}""" + "\n");

        return new CopilotImportSource(Config.Root, new CopilotPaths(Home, Tmp.PathTo("copilot")), [], new GitProviderRouter(), TimeProvider.System);
    }

    async Task<ImportCommand.ImportDiscoveryResult> DiscoverAsync(
            ProfileContext profiles, TestImportTerminal? terminal = null, IReadOnlyList<IImportSource>? sources = null) {
        ImportCommand.ImportDiscoveryResult? found = null;
        var exit = await new ImportCommand(Config.Root, profiles, Home,
                TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), router: new GitProviderRouter(),
                time: TimeProvider.System, accounts: TestAccounts.None, console: (terminal ?? new TestImportTerminal()).Console)
            .HandleImport(filterCwd: null, minLines: 1, sources: sources, discoverOnly: true, onDiscovered: r => found = r);

        await Assert.That(exit).IsEqualTo(0);

        return found!;
    }

    [Test]
    public async Task An_ignored_folder_is_counted_as_left_out_and_not_as_unmatched() {
        var scratch = Tmp.CreateDir("scratch");
        var secret  = Tmp.CreateDir("secret-project");
        WriteClaudeSession("11111111-1111-1111-1111-111111111111", scratch.Path);
        WriteClaudeSession("22222222-2222-2222-2222-222222222222", secret.Path);

        ImportCommand.ImportDiscoveryResult? found = null;
        var exit = await new ImportCommand(Config.Root, Resolutions.Of(new Profile { ExcludedPaths = [secret.Path] }), Home,
                TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), router: new GitProviderRouter(),
                time: TimeProvider.System, accounts: TestAccounts.None, console: new TestImportTerminal().Console)
            .HandleImport(filterCwd: null, minLines: 1, discoverOnly: true, onDiscovered: r => found = r);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(found!.Summary.UnmatchedCount).IsEqualTo(1);
        await Assert.That(found.Summary.UnmatchedFolders.Any(f => f.Folder?.Contains("secret-project") == true)).IsFalse();
        await Assert.That(found.Summary.IgnoredCount).IsEqualTo(1);
        await Assert.That(found.Summary.IgnoredFolders.Single().Folder).Contains("secret-project");
    }

    /// <summary>The import judges ignored paths on the recorded cwd alone, so a remap into an ignored folder still
    /// imports the session and discovery must count it.</summary>
    [Test]
    public async Task A_remap_into_an_ignored_folder_does_not_leave_the_session_out() {
        var secret = Tmp.CreateDir("secret-project");
        var moved  = Tmp.PathTo("moved-project");
        WriteClaudeSession("33333333-3333-3333-3333-333333333333", moved);

        var profile  = new Profile { ExcludedPaths = [secret.Path] };
        var profiles = new ProfileContext(
            new(null, "default", profile, null),
            new ProfileConfig { CwdRemap = [new CwdRemap { From = moved, To = secret.Path }] });

        ImportCommand.ImportDiscoveryResult? found = null;
        var exit = await new ImportCommand(Config.Root, profiles, Home,
                TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), router: new GitProviderRouter(),
                time: TimeProvider.System, accounts: TestAccounts.None, console: new TestImportTerminal().Console)
            .HandleImport(filterCwd: null, minLines: 1, discoverOnly: true, onDiscovered: r => found = r);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(found!.Summary.IgnoredCount).IsEqualTo(0);
        await Assert.That(found.Summary.UnmatchedCount).IsEqualTo(1);
    }

    /// <summary>A transcript that records no cwd is judged on its project folder's name, the cwd the import falls back to.</summary>
    [Test]
    public async Task A_session_without_a_recorded_cwd_is_judged_on_its_project_folder() {
        WriteClaudeSession("44444444-4444-4444-4444-444444444444", cwd: null, projectDir: "-kcapfallback-secret");

        var found = await DiscoverAsync(Resolutions.Of(new Profile { ExcludedPaths = ["/kcapfallback/secret"] }));

        await Assert.That(found.Summary.IgnoredCount).IsEqualTo(1);
    }

    /// <summary>The import remaps a cwd-less transcript's project folder before judging its repository, so an allow list
    /// admits the session when the remap lands in an allowed checkout. The decoded folder does not exist, so only the
    /// remapped one can resolve to a repository.</summary>
    [Test]
    public async Task A_session_without_a_recorded_cwd_is_judged_on_its_remapped_project_folder() {
        var checkout   = Tmp.CreateDir("widgets-checkout");
        using var repo = GitRepo.InitIn(checkout);
        repo.AddRemote("https://github.com/acme/widgets.git");
        WriteClaudeSession("99999999-9999-4999-8999-999999999999", cwd: null, projectDir: "-kcapfallback-moved");

        var found = await DiscoverAsync(new ProfileContext(
            new(null, "default", new Profile { AllowedRepos = ["acme/widgets"] }, null),
            new ProfileConfig { CwdRemap = [new CwdRemap { From = "/kcapfallback/moved", To = checkout.Path }] }));

        await Assert.That(found.Summary.IgnoredCount).IsEqualTo(0);
    }

    /// <summary>The import excludes a session whose recorded folder is in an excluded repository even when a remap
    /// moves it elsewhere, so discovery must leave it out too, named by that recorded folder. Without the remap the
    /// folder's own repository is what resolves, so the remap is what makes this test about the recorded folder.</summary>
    [Test]
    public async Task A_remap_out_of_an_excluded_repository_still_leaves_the_session_out() {
        var recorded = Tmp.CreateDir("old-checkout");
        var moved    = Tmp.CreateDir("new-place");
        using var repo = GitRepo.InitIn(recorded);
        repo.AddRemote("https://github.com/acme/secret.git");
        WriteClaudeSession("55555555-5555-5555-5555-555555555555", recorded.Path);

        var found = await DiscoverAsync(new ProfileContext(
            new(null, "default", new Profile { ExcludedRepos = ["acme/secret"] }, null),
            new ProfileConfig { CwdRemap = [new CwdRemap { From = recorded.Path, To = moved.Path }] }));

        await Assert.That(found.Summary.IgnoredCount).IsEqualTo(1);
        await Assert.That(found.Summary.UnmatchedCount).IsEqualTo(0);
        await Assert.That(found.Summary.IgnoredFolders.Single().Folder).Contains("old-checkout");
    }

    /// <summary>The import excludes a session whose folder is in an excluded repository even when its harness recorded
    /// another repository, so discovery must leave it out too.</summary>
    [Test]
    public async Task A_recorded_repository_does_not_hide_an_excluded_checkout() {
        const string id = "88888888-8888-4888-8888-888888888888";
        var checkout    = Tmp.CreateDir("secret-checkout");
        using var repo  = GitRepo.InitIn(checkout);
        repo.AddRemote("https://github.com/acme/secret.git");

        var source = WriteCopilotSession(id, checkout.Path, "acme/open");

        var found = await DiscoverAsync(Resolutions.Of(new Profile { ExcludedRepos = ["acme/secret"] }), sources: [source]);

        await Assert.That(found.Summary.IgnoredCount).IsEqualTo(1);
    }

    /// <summary>The import also excludes the checkout a remap moves such a session into, so discovery must leave it out
    /// and name it by its recorded folder. The recorded folder does not exist, so only the remapped checkout resolves.</summary>
    [Test]
    public async Task A_recorded_repository_does_not_hide_a_remapped_excluded_checkout() {
        const string id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        var gone        = Tmp.PathTo("gone-place");
        var checkout    = Tmp.CreateDir("secret-checkout");
        using var repo  = GitRepo.InitIn(checkout);
        repo.AddRemote("https://github.com/acme/secret.git");
        var source = WriteCopilotSession(id, gone, "acme/open");

        var found = await DiscoverAsync(new ProfileContext(
                new(null, "default", new Profile { ExcludedRepos = ["acme/secret"] }, null),
                new ProfileConfig { CwdRemap = [new CwdRemap { From = gone, To = checkout.Path }] }),
            sources: [source]);

        await Assert.That(found.Summary.IgnoredCount).IsEqualTo(1);
        await Assert.That(found.Summary.IgnoredFolders.Single().Folder).Contains("gone-place");
    }

    /// <summary>A remap cannot bring a session under an ignored path back into scope, so its missing folder is not
    /// named as one to remap. The folder must not exist, or the report has nothing to name either way.</summary>
    [Test]
    public async Task A_missing_folder_under_an_ignored_path_is_not_offered_for_a_remap() {
        var gone = Tmp.PathTo("gone-secret");
        WriteClaudeSession("66666666-6666-6666-6666-666666666666", gone);
        var terminal = new TestImportTerminal();

        var found = await DiscoverAsync(Resolutions.Of(new Profile { ExcludedPaths = [gone] }), terminal);

        await Assert.That(found.Summary.IgnoredCount).IsEqualTo(1);
        await Assert.That(terminal.Plain.ToString()).DoesNotContain("no longer exist");
    }

    /// <summary>An allow list rejects a folder that resolves to no repository, which a remap may fix, so that missing
    /// folder is still named.</summary>
    [Test]
    public async Task A_missing_folder_an_allow_list_rejects_is_still_offered_for_a_remap() {
        var gone = Tmp.PathTo("gone-project");
        WriteClaudeSession("77777777-7777-7777-7777-777777777777", gone);
        var terminal = new TestImportTerminal();

        var found = await DiscoverAsync(Resolutions.Of(new Profile { AllowedRepos = ["acme/widgets"] }), terminal);

        await Assert.That(found.Summary.IgnoredCount).IsEqualTo(1);
        await Assert.That(terminal.Plain.ToString()).Contains("no longer exist");
    }
}
