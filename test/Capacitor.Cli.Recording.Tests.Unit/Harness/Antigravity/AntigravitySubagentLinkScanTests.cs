using Capacitor.Cli.Recording.Harness.Antigravity;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Antigravity;

/// <summary>
/// The live-watcher scan that links Antigravity subagents from the parent transcript's
/// INVOKE_SUBAGENT steps (the spawn-time signal). Drives
/// <see cref="AntigravitySubagentLinks.ExtractAndPostSubagentLinks"/> directly, since
/// <c>ScanAntigravitySubagentLinks</c> is a thin wrapper over drained lines.
/// </summary>
public class AntigravitySubagentLinkScanTests {
    [Test]
    public async Task Scan_posts_each_invoke_child_once_and_dedupes() {
        var posted = new HashSet<string>(StringComparer.Ordinal);
        var sent   = new List<string>();
        var lines = new[] {
            """{"type":"PLANNER_RESPONSE","content":"thinking"}""",
            """{"type":"INVOKE_SUBAGENT","content":"Created the following subagents:\n{\"conversationId\":\"6111e615-3caa-4fe8-9d55-b85c43f2cf1f\"}"}""",
        };
        await AntigravitySubagentLinks.ExtractAndPostSubagentLinks(lines, posted, child => { sent.Add(child); return Task.FromResult(true); }, _ => { });
        await AntigravitySubagentLinks.ExtractAndPostSubagentLinks(lines, posted, child => { sent.Add(child); return Task.FromResult(true); }, _ => { }); // re-scan
        await Assert.That(sent).IsEquivalentTo(new List<string> { "6111e615-3caa-4fe8-9d55-b85c43f2cf1f" }); // once
    }

    [Test]
    public async Task Scan_failed_post_is_retried_next_scan() {
        var posted = new HashSet<string>(StringComparer.Ordinal);
        var attempts = 0;
        var lines = new[] { """{"type":"INVOKE_SUBAGENT","content":"{\"conversationId\":\"6111e615-3caa-4fe8-9d55-b85c43f2cf1f\"}"}""" };
        await AntigravitySubagentLinks.ExtractAndPostSubagentLinks(lines, posted, _ => { attempts++; return Task.FromResult(false); }, _ => { }); // fail
        await AntigravitySubagentLinks.ExtractAndPostSubagentLinks(lines, posted, _ => { attempts++; return Task.FromResult(true); }, _ => { });  // retry, succeed
        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(posted).Contains("6111e615-3caa-4fe8-9d55-b85c43f2cf1f");
    }
}
