using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

/// <summary>The desktop app reads <c>kcap local status --json</c> back into the reading the CLI made; anything it
/// cannot read is no answer, so the app falls back to its own reading rather than guessing.</summary>
public class LocalStatusJsonTests {
    public static IEnumerable<LocalServerCondition> Conditions() => Enum.GetValues<LocalServerCondition>();

    [Test]
    [MethodDataSource(nameof(Conditions))]
    public async Task Every_condition_reads_back_as_itself(LocalServerCondition condition) {
        var line = LocalStatusJson.From(false, "Not running.", ["a note"], new(condition, 9473)).Render();

        await Assert.That(LocalStatusJson.Parse(line)).IsEqualTo(new LocalServerReading(condition, 9473));
    }

    [Test]
    public async Task A_taken_port_keeps_its_holder_and_answer() {
        var reading = new LocalServerReading(LocalServerCondition.PortTaken, 9473, new PortHolder(4728, "powershell"), "a program that does not speak HTTP");

        var line = LocalStatusJson.From(false, "Stopped, and port 9473 is taken by another process.", [], reading).Render();

        await Assert.That(line).Contains("\"condition\":\"port_taken\"").And.Contains("\"holder_pid\":4728");
        await Assert.That(LocalStatusJson.Parse(line)).IsEqualTo(reading);
    }

    [Test]
    public async Task The_vectors_state_rides_beside_the_reading() {
        var failed  = new DateTimeOffset(2026, 11, 2, 9, 14, 5, TimeSpan.Zero);
        var reading = new LocalServerReading(LocalServerCondition.Installed, 9473);

        var line = LocalStatusJson.From(true, "Ready on http://localhost:9473.", [], reading,
            new EmbeddingVectors(VectorStorageState.Failing, null, failed)).Render();

        await Assert.That(line).Contains("\"vectors_state\":\"failing\"")
            .And.Contains("\"vectors_last_stored_at\":null")
            .And.Contains("\"vectors_last_failure_at\":\"2026-11-02T09:14:05");
        await Assert.That(LocalStatusJson.Parse(line)).IsEqualTo(reading);
    }

    [Test]
    public async Task No_vectors_report_leaves_the_fields_null() {
        var line = LocalStatusJson.From(false, "Stopped.", [], new(LocalServerCondition.Installed, 9473)).Render();

        await Assert.That(line).Contains("\"vectors_state\":null");
    }

    [Test]
    [Arguments("")]
    [Arguments("Usage: kcap local <subcommand>")]
    [Arguments("{\"ready\":false,\"headline\":\"x\",\"condition\":\"sleeping\",\"notes\":[]}")]
    [Arguments("{\"ready\":false,\"headline\":\"x\",\"notes\":[]}")]
    public async Task Output_that_is_not_a_known_status_is_no_answer(string stdout) {
        await Assert.That(LocalStatusJson.Parse(stdout)).IsNull();
    }
}
