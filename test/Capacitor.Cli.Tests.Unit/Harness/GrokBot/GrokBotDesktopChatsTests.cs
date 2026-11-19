using System.Text;
using Capacitor.Cli.Harness.GrokBot;

namespace Capacitor.Cli.Tests.Unit.Harness.GrokBot;

/// <summary>Pins how the desktop app's chat store is read: which files are chats, when one counts as
/// changed, and when a copy is known to hold its chat from the first entry.</summary>
public class GrokBotDesktopChatsTests {
    const string Chat = "4d30ec7f-1027-4a23-86d7-d70f37e5bfec";

    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>A store file name as the app writes it: the key in unpadded lowercase base32.</summary>
    static string FileFor(string key) {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var name   = new StringBuilder();
        var buffer = 0;
        var bits   = 0;

        foreach (var b in Encoding.UTF8.GetBytes(key)) {
            buffer = (buffer << 8) | b;
            bits  += 8;

            while (bits >= 5) {
                bits -= 5;
                name.Append(alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0) name.Append(alphabet[(buffer << (5 - bits)) & 31]);

        return name.Append(".blob").ToString();
    }

    static string ChatFile(string chatId = Chat) => FileFor($"sand.client.slice.account.github%7Cuser_01TEST.transcript.replicas.{chatId}");

    static string Replica(params int[] seqs) {
        var entries = string.Join(",", seqs.Select(s => $$"""{"kind":"message","id":"e{{s}}","role":"user","content":"m{{s}}","timestampMs":{{s}},"seq":{{s}} }"""));

        return "{\"schemaVersion\":1,\"value\":{\"entries\":[" + entries + "],\"persistedAt\":1}}";
    }

    GrokBotDesktopChats Store() => new(Tmp.Path);

    [Test]
    public async Task A_chat_file_names_its_chat() {
        await Assert.That(GrokBotDesktopPaths.ChatIdOf(ChatFile())).IsEqualTo(Chat);
    }

    [Test]
    [Arguments("sand.client.slice.ui-layout")]
    [Arguments("sand.client.slice.account.github%7Cuser_01TEST.roster.last-roster")]
    [Arguments("sand.client.slice.account.github%7Cuser_01TEST.transcript.replicas.")]
    public async Task A_store_file_that_is_not_a_chat_names_none(string key) {
        await Assert.That(GrokBotDesktopPaths.ChatIdOf(FileFor(key))).IsNull();
    }

    [Test]
    [Arguments("lockfile")]
    [Arguments("not-base32!.blob")]
    [Arguments("UPPERCASE.blob")]
    public async Task A_file_the_app_did_not_name_names_none(string fileName) {
        await Assert.That(GrokBotDesktopPaths.ChatIdOf(fileName)).IsNull();
    }

    [Test]
    public async Task Only_chat_files_are_listed() {
        Tmp.CreateFile(ChatFile(), Replica(1));
        Tmp.CreateFile(FileFor("sand.client.slice.ui-layout"), "{}");
        Tmp.CreateFile("lockfile");

        var agents = await Store().ListAgentsAsync(CancellationToken.None);

        await Assert.That(agents.Select(a => a.Id)).IsEquivalentTo([Chat]);
    }

    [Test]
    public async Task A_chat_that_grew_has_a_new_fingerprint() {
        var file   = Tmp.CreateFile(ChatFile(), Replica(1));
        var before = (await Store().ListAgentsAsync(CancellationToken.None)).Single().Fingerprint;

        await File.WriteAllTextAsync(file, Replica(1, 2));
        var after = (await Store().ListAgentsAsync(CancellationToken.None)).Single().Fingerprint;

        await Assert.That(after).IsNotEqualTo(before);
    }

    [Test]
    public async Task A_read_returns_what_follows_the_last_delivered_entry() {
        Tmp.CreateFile(ChatFile(), Replica(1, 2, 4, 5));

        var read = await Store().ReadSinceAsync(Chat, 2, CancellationToken.None);

        await Assert.That(read.Entries.Select(e => e.Seq)).IsEquivalentTo([4, 5]);
        await Assert.That(read.FromThreadStart).IsTrue();
    }

    [Test]
    public async Task A_copy_that_opens_past_the_first_entry_does_not_hold_the_chats_beginning() {
        Tmp.CreateFile(ChatFile(), Replica(40, 41));

        var read = await Store().ReadSinceAsync(Chat, -1, CancellationToken.None);

        await Assert.That(read.Entries.Count).IsEqualTo(2);
        await Assert.That(read.FromThreadStart).IsFalse();
    }

    [Test]
    [Arguments("{\"schemaVersion\":1,\"value\":{\"entries\":[{\"id\":\"e1\"")]
    [Arguments("{\"schemaVersion\":2,\"rows\":[]}")]
    public async Task A_file_caught_mid_write_or_in_another_shape_reads_as_nothing(string content) {
        Tmp.CreateFile(ChatFile(), content);

        await Assert.That((await Store().ReadSinceAsync(Chat, -1, CancellationToken.None)).Entries).IsEmpty();
    }

    [Test]
    public async Task A_chat_with_no_file_reads_as_nothing() {
        await Assert.That((await Store().ReadSinceAsync(Chat, -1, CancellationToken.None)).Entries).IsEmpty();
    }
}
