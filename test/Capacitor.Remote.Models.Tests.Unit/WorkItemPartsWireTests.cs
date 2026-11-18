using System.Text.Json;

namespace Capacitor.Remote.Models.Tests.Unit;

public class WorkItemPartsWireTests {
    /// <summary>A server that sends no Pages or session titles, or sends them as null, reads as none.</summary>
    [Test]
    [Arguments("""{"work_item":{"work_item_id":"wi-1","title":"Faster checkout"}}""")]
    [Arguments("""{"work_item":{"work_item_id":"wi-1","title":"Faster checkout"},"pages":null,"sessions":null}""")]
    public async Task A_parts_read_without_Pages_or_session_titles_has_none(string json) {
        var dto = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.WorkItemPartsDto)!;

        await Assert.That(dto.Pages).IsEmpty();
        await Assert.That(dto.Sessions).IsEmpty();
    }
}
