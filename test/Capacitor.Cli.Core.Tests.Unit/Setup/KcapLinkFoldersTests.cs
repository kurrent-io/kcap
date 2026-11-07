using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class KcapLinkFoldersTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>The inventory takes any rooted line, and the root itself has no folder.</summary>
    [Test]
    public async Task Adopted_links_add_their_folders_and_a_bare_root_adds_none() {
        const string link = "/opt/npm/bin/kcap";
        var inventory = new AdoptedLinkInventory(Config.Root);
        inventory.Add(link);
        inventory.Add("/");
        var home = new UserHome("/home/me");

        var folders = KcapLinkFolders.WithAdopted(home, Config.Root);

        await Assert.That(folders).IsEquivalentTo([.. KcapLinkFolders.For(home), Path.GetDirectoryName(link)!]);
    }
}
