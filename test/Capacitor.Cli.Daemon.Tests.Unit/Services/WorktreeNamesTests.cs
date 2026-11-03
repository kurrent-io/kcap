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
        await Assert.That(name).Matches("^[a-z]{3,5}-[a-z]{3,5}$");
    }

    /// <summary>Every plain name taken still yields one, so a crowded worktree root cannot stall a launch.</summary>
    [Test]
    public async Task PickFree_adds_a_suffix_once_plain_names_keep_colliding() {
        var name = WorktreeNames.PickFree(candidate => candidate.Split('-').Length == 2, new Random(7));

        await Assert.That(name).Matches("^[a-z]{3,5}-[a-z]{3,5}-[0-9a-f]{4}$");
    }

    /// <summary>The word lists stay short, plain lowercase and free of duplicates, which would skew the draw.</summary>
    [Test]
    [Arguments("adjectives")]
    [Arguments("nouns")]
    public async Task Words_are_three_to_five_lowercase_letters_and_unique(string list) {
        var words = list == "adjectives" ? WorktreeNames.Adjectives : WorktreeNames.Nouns;

        await Assert.That(words.Where(w => w.Length is < 3 or > 5 || !w.All(char.IsAsciiLetterLower))).IsEmpty();
        await Assert.That(words.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(words.Length);
    }
}
