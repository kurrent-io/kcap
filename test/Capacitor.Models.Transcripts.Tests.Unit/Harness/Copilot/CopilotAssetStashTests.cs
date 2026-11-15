using Capacitor.Models.Transcripts.Harness.Copilot;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Copilot;

/// <summary>The bounded store of attachment bytes awaiting their prompt: a lookup leaves the bytes in place, and the
/// oldest go first once the entry or byte cap is passed.</summary>
public class CopilotAssetStashTests {
    [Test]
    public async Task A_lookup_leaves_the_asset_in_place() {
        var stash = new CopilotAssetStash();
        stash.Put("a", "image/png", [1, 2, 3, 4]);

        var asset = stash.Get("a");

        await Assert.That(asset!.Value.MimeType).IsEqualTo("image/png");
        await Assert.That(asset.Value.Data).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
        await Assert.That(stash.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_repeated_id_keeps_the_first_bytes() {
        var stash = new CopilotAssetStash();
        stash.Put("a", "image/png", [1, 1, 1]);
        stash.Put("a", "image/png", [9, 9, 9, 9, 9]);

        await Assert.That(stash.Count).IsEqualTo(1);
        await Assert.That(stash.Get("a")!.Value.Data).IsEquivalentTo(new byte[] { 1, 1, 1 });
    }

    [Test]
    public async Task Past_the_entry_cap_the_oldest_goes() {
        var stash = new CopilotAssetStash();
        for (var i = 0; i <= CopilotAssetStash.MaxEntries; i++) stash.Put($"id{i}", "image/png", [(byte)i]);

        await Assert.That(stash.Count).IsEqualTo(CopilotAssetStash.MaxEntries);
        await Assert.That(stash.Get("id0")).IsNull();
        await Assert.That(stash.Get($"id{CopilotAssetStash.MaxEntries}")).IsNotNull();
    }

    [Test]
    public async Task Past_the_byte_cap_the_oldest_goes() {
        var stash = new CopilotAssetStash();
        stash.Put("big", "application/octet-stream", new byte[CopilotAssetStash.MaxBytes - 1]);
        stash.Put("small", "image/png", [1, 2]);

        await Assert.That(stash.Count).IsEqualTo(1);
        await Assert.That(stash.Get("big")).IsNull();
        await Assert.That(stash.Get("small")).IsNotNull();
    }
}
