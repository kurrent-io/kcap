using System.Net;
using System.Text;
using Capacitor.Cli.Recording.Harness.GrokBot;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.GrokBot;

/// <summary>Pins which <c>listAgents</c> fields count as a change. <c>lastEntry</c> previews how the last
/// message opens and <c>localActivityAt</c> is the chat's creation time, so a fingerprint built on them stays
/// the same while the chat grows.</summary>
public class GrokBotGatewayTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Canned(string body) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    async Task<GrokBotAgent> ListOne(string agentJson) {
        var descriptor = Tmp.CreateFile("gateway.json", """{"scheme":"http","host":"0.0.0.0","port":1340,"token":"t"}""");
        using var http = new HttpClient(new Canned($"[{agentJson}]"));
        return (await new GrokBotGateway(descriptor, http).ListAgentsAsync(CancellationToken.None)).Single();
    }

    const string Base = "\"id\":\"g1\",\"name\":\"weather\",\"isGroup\":true,\"isRunningTurn\":false,\"lastEntry\":{\"kind\":\"text\",\"text\":\"```\"},\"localActivityAt\":1";

    [Test]
    public async Task A_new_entry_changes_the_fingerprint() {
        var before = await ListOne($$"""{{{Base}},"newestEntryId":"t3s0","updatedAt":100}""");
        var after  = await ListOne($$"""{{{Base}},"newestEntryId":"t4u","updatedAt":200}""");

        await Assert.That(after.Fingerprint).IsNotEqualTo(before.Fingerprint);
    }

    [Test]
    public async Task A_finished_turn_changes_the_fingerprint() {
        var running = await ListOne("""{"id":"g1","name":"weather","isRunningTurn":true,"newestEntryId":"t4s0"}""");
        var idle    = await ListOne("""{"id":"g1","name":"weather","isRunningTurn":false,"newestEntryId":"t4s0"}""");

        await Assert.That(idle.Fingerprint).IsNotEqualTo(running.Fingerprint);
    }

    [Test]
    public async Task Preview_and_creation_time_alone_do_not_count_as_a_change() {
        var a = await ListOne("""{"id":"g1","name":"w","newestEntryId":"t4u","lastEntry":{"kind":"text","text":"```"},"localActivityAt":1}""");
        var b = await ListOne("""{"id":"g1","name":"w","newestEntryId":"t4u","lastEntry":{"kind":"text","text":"Sure"},"localActivityAt":2}""");

        await Assert.That(b.Fingerprint).IsEqualTo(a.Fingerprint);
    }

    /// <summary>Serves a chat of <paramref name="count"/> entries, seq 1 upward, in tail pages.</summary>
    /// <param name="stuckAt">A cursor the gateway hands out and then answers with an empty page.</param>
    /// <param name="unusable">Entries the gateway serves without a time, which the reader rejects.</param>
    sealed class Paged(int count, int pageSize, int? stuckAt = null, Func<int, bool>? unusable = null) : HttpMessageHandler {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Calls++;
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var before = body.RootElement.TryGetProperty("beforeSeq", out var b) ? b.GetInt32() : count + 1;
            var first  = before == stuckAt ? before : Math.Max(1, before - pageSize);
            var page   = string.Join(",", Enumerable.Range(first, before - first).Select(seq =>
                unusable?.Invoke(seq) == true ? $$"""{"id":"e{{seq}}","seq":{{seq}}}""" : $$"""{"id":"e{{seq}}","seq":{{seq}},"timestampMs":{{seq}}}"""));
            var cursor = first > 1 ? $",\"nextBeforeSeq\":{first}" : "";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"entries":[{{page}}]{{cursor}}}""", Encoding.UTF8, "application/json") };
        }
    }

    async Task<(GrokBotRead Read, int Calls)> ReadSince(int count, int lastSeq, int? stuckAt = null, Func<int, bool>? unusable = null) {
        var descriptor = Tmp.CreateFile("gateway.json", """{"scheme":"http","host":"127.0.0.1","port":1340,"token":"t"}""");
        var handler    = new Paged(count, pageSize: 10, stuckAt, unusable);
        using var http = new HttpClient(handler);

        return (await new GrokBotGateway(descriptor, http).ReadSinceAsync("g1", lastSeq, CancellationToken.None), handler.Calls);
    }

    [Test]
    public async Task A_first_read_pages_back_to_the_chats_first_entry() {
        var (read, calls) = await ReadSince(count: 35, lastSeq: -1);

        await Assert.That(read.Entries.Select(e => e.Seq).Order()).IsEquivalentTo(Enumerable.Range(1, 35));
        await Assert.That(read.FromThreadStart).IsTrue();
        await Assert.That(calls).IsEqualTo(4);
    }

    [Test]
    public async Task A_later_read_pages_back_only_to_what_was_delivered() {
        var (read, calls) = await ReadSince(count: 35, lastSeq: 20);

        await Assert.That(read.Entries.Min(e => e.Seq)).IsLessThanOrEqualTo(21);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task A_chat_longer_than_the_page_budget_is_read_without_its_beginning() {
        var (read, calls) = await ReadSince(count: GrokBotGateway.MaxPages * 10 + 5, lastSeq: -1);

        await Assert.That(read.FromThreadStart).IsFalse();
        await Assert.That(calls).IsEqualTo(GrokBotGateway.MaxPages);
    }

    [Test]
    public async Task A_cursor_that_leads_nowhere_leaves_the_read_without_its_beginning() {
        var (read, _) = await ReadSince(count: 35, lastSeq: -1, stuckAt: 16);

        await Assert.That(read.Entries.Min(e => e.Seq)).IsEqualTo(16);
        await Assert.That(read.FromThreadStart).IsFalse();
    }

    /// <summary>The budget is for a chat whose beginning is unknown. A resumed read that stopped at it would
    /// deliver the newest pages and pass over everything between them and what was delivered before.</summary>
    [Test]
    public async Task A_resumed_read_goes_back_to_what_was_delivered_however_far_that_is() {
        var count = GrokBotGateway.MaxPages * 10 + 50;

        var (read, calls) = await ReadSince(count, lastSeq: 5);

        await Assert.That(read.Failed).IsFalse();
        await Assert.That(read.Entries.Select(e => e.Seq).Where(seq => seq > 5).Order()).IsEquivalentTo(Enumerable.Range(6, count - 5));
        await Assert.That(calls).IsGreaterThan(GrokBotGateway.MaxPages);
    }

    [Test]
    public async Task A_resumed_read_that_cannot_get_back_to_what_was_delivered_is_unreadable() {
        var (read, _) = await ReadSince(count: 35, lastSeq: 5, stuckAt: 16);

        await Assert.That(read.Failed).IsTrue();
        await Assert.That(read.Entries).IsEmpty();
    }

    [Test]
    public async Task A_page_of_entries_the_reader_rejects_does_not_end_the_read() {
        var (read, _) = await ReadSince(count: 35, lastSeq: -1, unusable: seq => seq is >= 16 and <= 35);

        await Assert.That(read.Entries.Select(e => e.Seq).Order()).IsEquivalentTo(Enumerable.Range(1, 15));
        await Assert.That(read.FromThreadStart).IsTrue();
    }

    [Test]
    public async Task Groups_are_listed() {
        var group = await ListOne($$"""{{{Base}}}""");

        await Assert.That(group.Id).IsEqualTo("g1");
    }
}
