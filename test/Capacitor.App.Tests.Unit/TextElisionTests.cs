using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class TextElisionTests {
    [Test]
    public async Task Shorter_than_the_budget_is_returned_unchanged() {
        await Assert.That(TextElision.Middle("", 10)).IsEqualTo("");
        await Assert.That(TextElision.Middle("git status", 10)).IsEqualTo("git status");
    }

    [Test]
    public async Task Both_ends_survive_the_cut() {
        await Assert.That(TextElision.Middle("abcdefghij", 5)).IsEqualTo("ab…ij");
        await Assert.That(TextElision.Middle("abcdefghij", 6)).IsEqualTo("abc…ij");
    }

    /// Half a surrogate pair renders as a replacement box, so the pair is dropped whole — on
    /// either side of the ellipsis, since the head and the tail are cut independently.
    [Test]
    public async Task Never_splits_a_surrogate_pair_on_either_side() {
        var atHead = new string('x', 39) + "\U0001F600" + new string('y', 40);
        var head = TextElision.Middle(atHead, 80);
        await Assert.That(head).IsEqualTo(new string('x', 39) + "…" + new string('y', 39));

        var atTail = new string('x', 41) + "\U0001F600" + new string('y', 38);
        var tail = TextElision.Middle(atTail, 80);
        await Assert.That(tail).IsEqualTo(new string('x', 40) + "…" + new string('y', 38));
    }

    [Test]
    [Arguments("src/Capacitor.App/ViewModels/ToolDetail.cs", 40, "src/…/ViewModels/ToolDetail.cs")]
    [Arguments("/Users/dev/src/kcap-cli/.capacitor/worktrees/long-name/src/Capacitor.App/ViewModels/ToolDetail.cs", 50, "/Users/…/Capacitor.App/ViewModels/ToolDetail.cs")]
    [Arguments("/Users/dev/src/a-really-long-folder-name/ToolDetail.cs", 20, "…/ToolDetail.cs")]
    [Arguments(@"C:\Users\dev\src\kcap-cli\src\Capacitor.App\ToolDetail.cs", 32, @"C:\…\Capacitor.App\ToolDetail.cs")]
    [Arguments("src/ToolDetail.cs", 40, "src/ToolDetail.cs")]
    public async Task A_path_loses_whole_folders_from_its_middle(string path, int max, string expected) =>
        await Assert.That(TextElision.Path(path, max)).IsEqualTo(expected);

    /// With no folder to give up, the file name itself is cut rather than overflowing the budget.
    [Test]
    public async Task A_file_name_longer_than_the_budget_is_cut_in_its_middle() {
        var cut = TextElision.Path("src/" + new string('f', 60) + ".cs", 20);
        await Assert.That(cut.Length).IsEqualTo(20);
        await Assert.That(cut.EndsWith(".cs", StringComparison.Ordinal)).IsTrue();
    }
}
