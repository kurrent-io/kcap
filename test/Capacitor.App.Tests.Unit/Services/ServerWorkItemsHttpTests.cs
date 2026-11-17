using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit.Services;

public class ServerWorkItemsHttpTests {
    [Test]
    public async Task A_session_is_labelled_with_its_part_or_no_part_on_the_work_item_itself() {
        var mapped = ServerWorkItemsHttp.Map([
            new ProjectWorkItemDto {
                WorkItemId = "approve", Title = "Approve requests from your phone", PartsDone = 2, PartsTotal = 6,
                Sessions = [new() { SessionId = "build", PartId = "list" }, new() { SessionId = "plan" }],
                Parts = [new() { WorkItemId = "list", Title = "List every decision waiting on you" }],
            },
        ]);
        await Assert.That(mapped[0].PartBySession["build"]).IsEqualTo("List every decision waiting on you");
        await Assert.That(mapped[0].PartBySession["plan"]).IsEqualTo(RailWorkItem.NoPart);
    }
}
