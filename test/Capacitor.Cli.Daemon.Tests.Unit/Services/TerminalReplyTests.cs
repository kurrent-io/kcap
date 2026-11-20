using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class TerminalReplyTests {
    [Test]
    [Arguments("\e[12;40R")]
    [Arguments("\e[0n")]
    [Arguments("\e[?12;40;1R")]
    [Arguments("\e[?10n")]
    [Arguments("\e[?62;22c")]
    [Arguments("\e[>0;276;0c")]
    [Arguments("\e[?2004;1$y")]
    [Arguments("\e[?1u")]
    [Arguments("\e[8;40;120t")]
    [Arguments("\e[I")]
    [Arguments("\e[O")]
    [Arguments("\e]11;rgb:0000/0000/0000\a")]
    [Arguments("\e]10;rgb:ffff/ffff/ffff\e\\")]
    [Arguments("\eP>|xterm(390)\e\\")]
    [Arguments("\e[I\e[12;40R")]
    public async Task A_terminal_reply_is_recognised(string data) =>
        await Assert.That(TerminalReply.IsOnlyReplies(Encoding.UTF8.GetBytes(data))).IsTrue();

    /// <summary>What a key or mouse action encodes to, alone or after a reply, is not one.</summary>
    [Test]
    [Arguments("x")]
    [Arguments("\r")]
    [Arguments("\e")]
    [Arguments("\e[A")]
    [Arguments("\e[3~")]
    [Arguments("\e[97;5u")]
    [Arguments("\e[<64;10;5M")]
    [Arguments("\e[200~text\e[201~")]
    [Arguments("\e[12;40Rx")]
    [Arguments("\e]11;unterminated")]
    [Arguments("")]
    public async Task Typed_input_is_not_a_reply(string data) =>
        await Assert.That(TerminalReply.IsOnlyReplies(Encoding.UTF8.GetBytes(data))).IsFalse();
}
