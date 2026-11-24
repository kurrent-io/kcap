using Capacitor.Cli.Core.Harness.GrokBot;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.GrokBot;

/// <summary>Pins which files in the Grok Bot desktop app's store are chats, and which chat each one holds.</summary>
public class GrokBotDesktopPathsTests {
    const string Chat = "4d30ec7f-1027-4a23-86d7-d70f37e5bfec";

    [Test]
    public async Task A_chat_file_names_its_chat() {
        await Assert.That(GrokBotDesktopPaths.ChatIdOf(GrokBotChatFile.NameOfChat(Chat))).IsEqualTo(Chat);
    }

    [Test]
    [Arguments("sand.client.slice.ui-layout")]
    [Arguments("sand.client.slice.account.github%7Cuser_01TEST.roster.last-roster")]
    [Arguments("sand.client.slice.account.github%7Cuser_01TEST.transcript.replicas.")]
    public async Task A_store_file_that_is_not_a_chat_names_none(string key) {
        await Assert.That(GrokBotDesktopPaths.ChatIdOf(GrokBotChatFile.NameFor(key))).IsNull();
    }

    [Test]
    [Arguments("lockfile")]
    [Arguments("not-base32!.blob")]
    [Arguments("UPPERCASE.blob")]
    public async Task A_file_the_app_did_not_name_names_none(string fileName) {
        await Assert.That(GrokBotDesktopPaths.ChatIdOf(fileName)).IsNull();
    }
}
