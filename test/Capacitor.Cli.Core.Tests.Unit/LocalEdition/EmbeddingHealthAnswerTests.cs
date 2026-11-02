using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

/// <summary>The <c>vectors</c> object is optional: an older server omits it and a newer one may name a state this
/// build does not know, and neither makes the report unreadable.</summary>
public class EmbeddingHealthAnswerTests {
    [Test]
    public async Task A_failing_pass_reads_with_its_times() {
        var answer = EmbeddingHealthAnswer.Parse("""
            {"provider":"builtin","usable":true,"reason":null,"probe_head":null,"probe":null,
             "vectors":{"state":"failing","last_stored_at":null,"last_failure_at":"2026-11-02T09:14:05+00:00"}}
            """);

        await Assert.That(answer).IsEqualTo(new EmbeddingHealthAnswer("builtin", true, null,
            new EmbeddingVectors(VectorStorageState.Failing, null, new DateTimeOffset(2026, 11, 2, 9, 14, 5, TimeSpan.Zero))));
    }

    [Test]
    [Arguments("not_run", VectorStorageState.NotRun)]
    [Arguments("ok", VectorStorageState.Ok)]
    [Arguments("failing", VectorStorageState.Failing)]
    [Arguments("paused", VectorStorageState.Unknown)]
    public async Task Each_state_reads_by_its_wire_name(string wire, VectorStorageState state) {
        var answer = EmbeddingHealthAnswer.Parse($$$"""{"provider":"ollama","usable":true,"vectors":{"state":"{{{wire}}}"}}""");

        await Assert.That(answer?.Vectors?.State).IsEqualTo(state);
    }

    [Test]
    public async Task A_vectors_object_without_a_state_is_unknown() {
        var answer = EmbeddingHealthAnswer.Parse("""{"provider":"builtin","usable":true,"vectors":{"last_stored_at":"not a time"}}""");

        await Assert.That(answer?.Vectors).IsEqualTo(new EmbeddingVectors(VectorStorageState.Unknown));
    }

    [Test]
    [Arguments("""{"provider":"builtin","usable":true,"reason":null,"probe_head":null}""")]
    [Arguments("""{"provider":"builtin","usable":true,"vectors":null}""")]
    [Arguments("""{"provider":"builtin","usable":true,"vectors":"failing"}""")]
    public async Task An_older_server_leaves_the_vectors_unknown(string body) {
        var answer = EmbeddingHealthAnswer.Parse(body);

        await Assert.That(answer).IsEqualTo(new EmbeddingHealthAnswer("builtin", true, null));
    }

    [Test]
    [Arguments("{nope")]
    [Arguments("""{"usable":true,"vectors":{"state":"ok"}}""")]
    [Arguments("[]")]
    public async Task A_body_that_is_not_the_report_is_no_answer(string body) {
        await Assert.That(EmbeddingHealthAnswer.Parse(body)).IsNull();
    }
}
