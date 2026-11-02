using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class WorktreeNamesTests {
    [Test]
    public async Task PickFree_draws_again_until_a_name_is_free() {
        var rejected = new List<string>();

        var name = WorktreeNames.PickFree(candidate => {
            if (rejected.Count >= 3) return false;
            rejected.Add(candidate);

            return true;
        }, new Random(7));

        await Assert.That(rejected).Count().IsEqualTo(3);
        await Assert.That(rejected).DoesNotContain(name);
        await Assert.That(name).Matches("^[a-z]+-[a-z]+-[a-z]+$");
    }

    /// <summary>Every plain name taken still yields one, so a crowded worktree root cannot stall a launch.</summary>
    [Test]
    public async Task PickFree_adds_a_suffix_once_plain_names_keep_colliding() {
        var name = WorktreeNames.PickFree(candidate => candidate.Split('-').Length == 3, new Random(7));

        await Assert.That(name).Matches("^[a-z]+-[a-z]+-[a-z]+-[0-9a-f]{4}$");
    }
}
