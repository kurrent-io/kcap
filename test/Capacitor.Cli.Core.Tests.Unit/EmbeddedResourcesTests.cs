using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Tests.Unit;

public class EmbeddedResourcesTests {
    const string Help = "Usage\r\n#if windows\r\nScheduled Task\r\n#endif\r\n#if unix\r\nLaunchAgent\r\n#endif\r\nSubcommands\r\n";

    [Test]
    [Arguments(true, "Usage\r\nScheduled Task\r\nSubcommands\r\n")]
    [Arguments(false, "Usage\r\nLaunchAgent\r\nSubcommands\r\n")]
    public async Task Help_keeps_only_this_platforms_block_and_drops_the_markers(bool windows, string expected) =>
        await Assert.That(EmbeddedResources.ForPlatform(Help, windows)).IsEqualTo(expected);

    [Test]
    public async Task The_local_help_names_one_platforms_service() {
        var windows = EmbeddedResources.ForPlatform(EmbeddedResources.Load("help-local.txt"), windows: true);
        var unix    = EmbeddedResources.ForPlatform(EmbeddedResources.Load("help-local.txt"), windows: false);

        await Assert.That(windows).Contains("Scheduled Task");
        await Assert.That(windows).DoesNotContain("launchd");
        await Assert.That(unix).Contains("launchd");
        await Assert.That(unix).DoesNotContain("Scheduled Task");
        await Assert.That(windows + unix).DoesNotContain("#if");
    }

    /// <summary>A count of kcap MCP servers goes stale with every server added, so no help text states one.</summary>
    [Test]
    public async Task No_help_text_counts_the_kcap_MCP_servers() {
        var count = new Regex(@"\b(\d+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\s+kcap\s+MCP\s+servers\b",RegexOptions.IgnoreCase);
        const string prefix = "Capacitor.Cli.Core.Resources.";

        var helps = typeof(EmbeddedResources).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix + "help-", StringComparison.Ordinal) && n.EndsWith(".txt", StringComparison.Ordinal))
            .Select(n => n[prefix.Length..])
            .ToList();

        await Assert.That(helps).Contains("help-plugin.txt");

        foreach (var name in helps)
            await Assert.That(count.Match(EmbeddedResources.Load(name)).Value).IsEmpty().Because(name);
    }
}
