namespace Capacitor.Cli.Core.Tests.Unit;

public class SessionIdsTests {
    [Test]
    public async Task Dashed_guid_becomes_its_n_form() =>
        await Assert.That(SessionIds.Canonical("8BC7255F-2453-4EFD-A733-0AF4B6AE9F20")).IsEqualTo("8bc7255f24534efda7330af4b6ae9f20");

    [Test]
    public async Task Opaque_id_is_returned_trimmed_and_unchanged() =>
        await Assert.That(SessionIds.Canonical("  sess-1 ")).IsEqualTo("sess-1");

    [Test]
    [Arguments("sess-1", "sess-1", true)]
    [Arguments("8bc7255f24534efda7330af4b6ae9f20", "8BC7255F-2453-4EFD-A733-0AF4B6AE9F20", true)]
    [Arguments("senc1vptdh45epitvqf2ovpurmdnpg3c", "sess-1", true)]
    [Arguments("senc1vptdh45epitvqf2ovpurmdnpg3c", " sess-1 ", true)]
    [Arguments("senc1vptdh45epitvqf2ovpurmdnpg3c", "sess-2", false)]
    [Arguments("sess-2", "sess-1", false)]
    [Arguments(null, "sess-1", false)]
    public async Task A_reported_id_names_the_sent_session_raw_canonical_or_encoded(string? reported, string sent, bool same) =>
        await Assert.That(SessionIds.SameSession(reported, sent)).IsEqualTo(same);

    [Test]
    [Arguments("5eed0001000040008000000000000001", "5eed0001-0000-4000-8000-000000000001")]
    [Arguments("ses_3f2a1b", "ses_3f2a1b")]
    [Arguments("senc1vptdh45epitvqf2ovpurmdnpg3c", "senc1vptdh45epitvqf2ovpurmdnpg3c")]
    public async Task A_canonical_guid_is_shown_dashed_and_an_opaque_id_as_is(string id, string shown) =>
        await Assert.That(SessionIds.Display(id)).IsEqualTo(shown);

    [Test]
    public async Task Null_and_blank_are_null() {
        await Assert.That(SessionIds.Canonical(null)).IsNull();
        await Assert.That(SessionIds.Canonical("   ")).IsNull();
    }
}
