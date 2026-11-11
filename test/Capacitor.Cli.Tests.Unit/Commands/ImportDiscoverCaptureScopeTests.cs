using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary><c>kcap import --discover</c> reports a folder <c>kcap ignore</c> covers as left out, never as a folder
/// to import or remap.</summary>
public class ImportDiscoverCaptureScopeTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    void WriteClaudeSession(string sessionId, string cwd) =>
        Home.CreateDir(".claude", "projects", "-discover-" + sessionId[..8]).CreateFile($"{sessionId}.jsonl", [
            .. Enumerable.Range(0, 5).Select(i => new JsonObject {
                ["type"]      = "user",
                ["sessionId"] = sessionId,
                ["timestamp"] = $"2026-03-15T10:00:0{i}Z",
                ["cwd"]       = cwd,
                ["message"]   = new JsonObject { ["content"] = $"step {i}" },
            }.ToJsonString()),
        ]);

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
}
