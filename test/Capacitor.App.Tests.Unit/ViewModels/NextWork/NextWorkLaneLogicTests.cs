using Capacitor.App.ViewModels.NextWork;

namespace Capacitor.App.Tests.Unit.ViewModels.NextWork;

public class NextWorkLaneLogicTests {
    static NextWorkRow Row(string kind, string arm, bool bug, int? priority) =>
        new("k:1", kind, "1", "Label", null, "h1", arm, 3, 1, "", new(0, priority, bug, 0, 0), []);

    [Test]
    public async Task A_bug_whose_icon_is_the_bug_glyph_carries_only_its_priority_mark() {
        var row = Row("work_item", "Assigned", bug: true, priority: 2);

        await Assert.That(NextWorkLaneLogic.Glyph(row).Icon).IsEqualTo("bug");
        await Assert.That(NextWorkLaneLogic.RowMarks(row)).IsEquivalentTo(["High"]);
    }

    [Test]
    public async Task A_bug_shown_under_another_icon_keeps_its_bug_mark() {
        var row = Row("pr", "ReviewRequested", bug: true, priority: 3);

        await Assert.That(NextWorkLaneLogic.Glyph(row).Icon).IsEqualTo("pr");
        await Assert.That(NextWorkLaneLogic.RowMarks(row)).IsEquivalentTo(["Medium", "Bug"]);
    }
}
