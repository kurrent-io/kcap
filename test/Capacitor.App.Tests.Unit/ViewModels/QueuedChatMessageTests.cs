using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class QueuedChatMessageTests {
    [Test]
    public async Task An_unknown_turn_does_not_claim_the_agent_is_idle_or_working() {
        var row = new QueuedChatMessage("hello", 0, 0, 0, []);
        row.SetPickup(null, true, true, false);
        await Assert.That(row.PickupNote).IsEqualTo(QueuedChatMessage.UnknownPickupNote);
        await Assert.That(row.CanSendNow).IsTrue();
    }
    /// A send the server attributed has no feed line to match its echo by, so its echo is read in
    /// every form the receipt takes: a slash command rejoined with single spaces, a bang command
    /// without its space, or a prompt followed by its attachment trailer.
    [Test]
    [Arguments("/cmd   arg", "/cmd arg", false)]
    [Arguments("! ls", "!ls", false)]
    [Arguments("look", "look\n\n[Attached files: .attached/x/a.png]", true)]
    [Arguments("hello", "hello", false)]
    public async Task An_echo_matches_in_any_form_its_receipt_takes(string sent, string echo, bool attached) {
        var row = new QueuedChatMessage(sent, 0, 0, 0, attached ? [Guid.NewGuid()] : []);
        await Assert.That(row.MatchesEcho(echo)).IsTrue();
        await Assert.That(row.MatchesEcho("something else")).IsFalse();
    }

    /// A queued prompt that was only attachments reaches the app with empty text, and its row must
    /// still say what it is.
    [Test]
    [Arguments("do it", "do it")]
    [Arguments("", QueuedChatMessage.AttachmentsOnlyText)]
    [Arguments(" \n", QueuedChatMessage.AttachmentsOnlyText)]
    public async Task A_row_without_text_shows_the_attachments_placeholder(string text, string shown) {
        var row = QueuedChatMessage.FromServer(new QueuedInputItem { DispatchId = Guid.NewGuid(), Text = text });

        await Assert.That(row.DisplayText).IsEqualTo(shown);
    }
}
