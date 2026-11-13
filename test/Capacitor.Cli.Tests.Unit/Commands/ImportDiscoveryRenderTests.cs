using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The two renderings of the discovery report. The JSON one is a contract someone else's code reads,
/// so its field names are asserted rather than assumed.
/// </summary>
public class ImportDiscoveryRenderTests {
    static ImportDiscoverySummary Sample() =>
        ImportDiscoverySummary.Build(
            [("a", new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero)),
             ("b", new DateTimeOffset(2026, 1, 4, 9, 0, 0, TimeSpan.Zero)),
             ("c", null)],
            new Dictionary<string, (string Owner, string Name)?> {
                ["a"] = ("EventStore", "kcap"),
                ["b"] = ("EventStore", "kcap"),
                ["c"] = null,
            },
            [new ImportDiscoveryWindow(FirstRunImportWindows.Last30, new DateOnly(2026, 2, 1)),
             new ImportDiscoveryWindow(FirstRunImportWindows.Everything, null)]);

    [Test]
    public async Task Json_carries_the_repo_totals_the_unmatched_count_and_every_window() {
        var root = JsonNode.Parse(ImportDiscoveryRender.ToJson(Sample()))!.AsObject();

        var repo = root["repos"]!.AsArray()[0]!.AsObject();

        await Assert.That(repo["owner"]!.GetValue<string>()).IsEqualTo("EventStore");
        await Assert.That(repo["name"]!.GetValue<string>()).IsEqualTo("kcap");
        await Assert.That(repo["sessions"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(repo["last_session_at"]!.GetValue<string>()).StartsWith("2026-03-01");

        await Assert.That(root["unmatched_sessions"]!.GetValue<int>()).IsEqualTo(1);

        var windows = root["windows"]!.AsArray();

        await Assert.That(windows.Count).IsEqualTo(2);
        await Assert.That(windows[0]!["since"]!.GetValue<string>()).IsEqualTo("2026-02-01");
        await Assert.That(windows[1]!["since"]).IsNull();
    }

    /// A selection's count is a cell — this repository in that window — which neither margin gives.
    [Test]
    public async Task Json_carries_each_repos_own_count_per_window() {
        var root    = JsonNode.Parse(ImportDiscoveryRender.ToJson(Sample()))!.AsObject();
        var windows = root["repos"]!.AsArray()[0]!["windows"]!.AsArray();

        await Assert.That(windows.Count).IsEqualTo(2);
        await Assert.That(windows[0]!["since"]!.GetValue<string>()).IsEqualTo("2026-02-01");
        await Assert.That(windows[0]!["sessions"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(windows[1]!["since"]).IsNull();
        await Assert.That(windows[1]!["sessions"]!.GetValue<int>()).IsEqualTo(2);
    }

    /// The desktop app offers each harness's history as its own choice and imports with that harness's
    /// flag, so a share is keyed by the id the flag is spelled with.
    [Test]
    public async Task Json_carries_each_harness_share_of_a_repo_under_its_flag_id() {
        var summary = ImportDiscoverySummary.Build(
            [("a", new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero)),
             ("b", new DateTimeOffset(2026, 1, 4, 9, 0, 0, TimeSpan.Zero))],
            new Dictionary<string, (string Owner, string Name)?> {
                ["a"] = ("EventStore", "kcap"),
                ["b"] = ("EventStore", "kcap"),
            },
            [new ImportDiscoveryWindow(FirstRunImportWindows.Last30, new DateOnly(2026, 2, 1)),
             new ImportDiscoveryWindow(FirstRunImportWindows.Everything, null)],
            new Dictionary<string, HarnessId> { ["a"] = HarnessId.MistralVibe, ["b"] = HarnessId.Codex });

        var vendors = JsonNode.Parse(ImportDiscoveryRender.ToJson(summary))!["repos"]!.AsArray()[0]!["vendors"]!.AsArray();

        await Assert.That(vendors.Count).IsEqualTo(2);
        await Assert.That(vendors[0]!["vendor"]!.GetValue<string>()).IsEqualTo("codex");
        await Assert.That(vendors[0]!["sessions"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(vendors[0]!["last_session_at"]!.GetValue<string>()).StartsWith("2026-01-04");
        await Assert.That(vendors[0]!["windows"]!.AsArray()[0]!["sessions"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(vendors[1]!["vendor"]!.GetValue<string>()).IsEqualTo("mistral-vibe");
        await Assert.That(vendors[1]!["windows"]!.AsArray()[0]!["sessions"]!.GetValue<int>()).IsEqualTo(1);
    }

    /// A folder carries the same split, so a caller can count and import it by the harnesses chosen; the
    /// sessions with no folder recorded cannot be named, so they carry none.
    [Test]
    public async Task Json_carries_each_harness_share_of_a_folder_under_its_flag_id() {
        var summary = ImportDiscoverySummary.Build(
            [("a", new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero)),
             ("b", new DateTimeOffset(2026, 1, 4, 9, 0, 0, TimeSpan.Zero)),
             ("c", null)],
            new Dictionary<string, (string Owner, string Name)?> { ["a"] = null, ["b"] = null, ["c"] = null },
            [new ImportDiscoveryWindow(FirstRunImportWindows.Last30, new DateOnly(2026, 2, 1)),
             new ImportDiscoveryWindow(FirstRunImportWindows.Everything, null)],
            new Dictionary<string, HarnessId> { ["a"] = HarnessId.Pi, ["b"] = HarnessId.Claude, ["c"] = HarnessId.Claude },
            new Dictionary<string, string> { ["a"] = "/w/scratch", ["b"] = "/w/scratch" });

        var folders = JsonNode.Parse(ImportDiscoveryRender.ToJson(summary))!["unmatched_folders"]!.AsArray();
        var vendors = folders[0]!["vendors"]!.AsArray();

        await Assert.That(vendors.Select(v => v!["vendor"]!.GetValue<string>())).IsEquivalentTo(["claude", "pi"]);
        await Assert.That(vendors[1]!["sessions"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(vendors[1]!["windows"]!.AsArray()[0]!["sessions"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(folders[1]!["folder"]).IsNull();
        await Assert.That(folders[1]!["vendors"]).IsNull();
    }

    [Test]
    public async Task Json_names_everything_as_a_null_window_rather_than_omitting_it() {
        // A reader has to be able to tell "no cap" from "a window that happens to hold everything".
        var root    = JsonNode.Parse(ImportDiscoveryRender.ToJson(Sample()))!.AsObject();
        var lastAll = root["windows"]!.AsArray()[1]!.AsObject();

        await Assert.That(lastAll["since"]).IsNull();
        await Assert.That(lastAll["sessions"]!.GetValue<int>()).IsEqualTo(3);
    }

    [Test]
    public async Task Text_reports_the_unmatched_count_because_a_repo_scope_silently_drops_it() {
        var text = ImportDiscoveryRender.ToText(Sample());

        await Assert.That(text).Contains("EventStore/kcap");
        await Assert.That(text).Contains("2 sessions");
        await Assert.That(text).Contains("couldn't match to a repository: 1");
        await Assert.That(text).Contains("everything");
    }

    [Test]
    public async Task Text_says_so_when_nothing_could_be_attributed() {
        var summary = ImportDiscoverySummary.Build(
            [("a", null)],
            new Dictionary<string, (string Owner, string Name)?> { ["a"] = null },
            [new ImportDiscoveryWindow(FirstRunImportWindows.Everything, null)]);

        await Assert.That(ImportDiscoveryRender.ToText(summary))
                    .Contains("no sessions could be attributed to a repository");
    }

    static ImportDiscoverySummary WithAnIgnoredFolder() =>
        ImportDiscoverySummary.Build(
            [("a", null), ("b", null), ("c", null)],
            new Dictionary<string, (string Owner, string Name)?>(),
            [new ImportDiscoveryWindow(FirstRunImportWindows.Everything, null)],
            folderBySession: new Dictionary<string, string> { ["a"] = "/w/scratch", ["b"] = "/w/secret", ["c"] = "/w/secret" },
            ignoredSessions: new HashSet<string> { "b", "c" });

    [Test]
    public async Task Text_lists_an_ignored_folder_as_left_out_not_as_unmatched() {
        var lines = ImportDiscoveryRender.ToText(WithAnIgnoredFolder()).Split(Environment.NewLine);

        var unmatched = Array.FindIndex(lines, l => l.Contains("couldn't match to a repository: 1"));
        var leftOut   = Array.FindIndex(lines, l => l.Contains("left out by kcap ignore, excluded_repos or an allow list: 2"));

        await Assert.That(unmatched).IsGreaterThanOrEqualTo(0);
        await Assert.That(leftOut).IsGreaterThan(unmatched);
        await Assert.That(lines[unmatched..leftOut].Any(l => l.Contains("secret"))).IsFalse();
        await Assert.That(lines[leftOut + 1]).IsEqualTo("    /w/secret  (2 sessions)");
        await Assert.That(lines[(leftOut + 1)..].Any(l => l.Contains("kcap remap"))).IsFalse();
    }

    [Test]
    public async Task Json_carries_the_ignored_sessions_apart_from_the_unmatched_ones() {
        var root = JsonNode.Parse(ImportDiscoveryRender.ToJson(WithAnIgnoredFolder()))!.AsObject();

        await Assert.That(root["unmatched_sessions"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(root["ignored"]!["count"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(root["ignored"]!["folders"]!.AsArray()[0]!["folder"]!.GetValue<string>()).IsEqualTo("/w/secret");
        await Assert.That(root["ignored"]!["folders"]!.AsArray()[0]!["sessions"]!.GetValue<int>()).IsEqualTo(2);
    }
}
