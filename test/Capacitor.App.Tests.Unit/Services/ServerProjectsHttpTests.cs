using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit.Services;

public class ServerProjectsHttpTests {
    [Test]
    public async Task Only_projects_the_person_belongs_to_are_offered() {
        var mapped = ServerProjectsHttp.Map([
            new ProjectSummaryDto { ProjectId = "a", Name = "owned", ViewerMembership = "owner" },
            new ProjectSummaryDto { ProjectId = "b", Name = "joined", ViewerMembership = "member" },
            new ProjectSummaryDto { ProjectId = "c", Name = "browsable", ViewerMembership = "none" },
        ]);
        await Assert.That(mapped.Select(p => p.Name)).IsEquivalentTo(["owned", "joined"]);
    }
}
