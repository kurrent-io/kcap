using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class AdoptedLinkInventoryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Added_links_survive_a_reload_and_removal_drops_only_that_link() {
        var first  = "/opt/npm/bin/kcap";
        var second = "/home/me/.nvm/versions/node/v22/bin/kcap";

        var inventory = new AdoptedLinkInventory(Config.Root);
        inventory.Add(first);
        inventory.Add(second);
        inventory.Add(first);

        await Assert.That(new AdoptedLinkInventory(Config.Root).Links()).IsEquivalentTo([first, second]);

        inventory.Remove(first);

        await Assert.That(new AdoptedLinkInventory(Config.Root).Links()).IsEquivalentTo([second]);
    }

    [Test]
    public async Task A_missing_or_unreadable_inventory_reads_as_empty() {
        await Assert.That(new AdoptedLinkInventory(Config.Root).Links()).IsEmpty();

        Directory.CreateDirectory(Config.Root.Path("adopted-kcap-links"));

        await Assert.That(new AdoptedLinkInventory(Config.Root).Links()).IsEmpty();
    }
}
