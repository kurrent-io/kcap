using Capacitor.Cli.Core.Harness.Dsh;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Dsh;

public class DshSessionIdTests {
    [Test]
    [Arguments("session-e1d79e8a-9b62-4b23-b576-7e7493c09dba",      "e1d79e8a9b624b23b5767e7493c09dba")]
    [Arguments("main-session-3bc87030-2c61-4183-9711-358334dd48d3", "3bc870302c6141839711358334dd48d3")]
    [Arguments("e1d79e8a-9b62-4b23-b576-7e7493c09dba",              "e1d79e8a9b624b23b5767e7493c09dba")]
    [Arguments("e1d79e8a9b624b23b5767e7493c09dba",                  "e1d79e8a9b624b23b5767e7493c09dba")]
    [Arguments("kcap-live-poc-1",                                   "kcap-live-poc-1")]
    public async Task Canonicalize_extracts_the_guid_or_keeps_a_short_id(string raw, string expected) =>
        await Assert.That(DshSessionId.Canonicalize(raw)).IsEqualTo(expected);

    [Test]
    [Arguments("session-e1d79e8a-9b62-4b23-b576-7e7493c09dba")]
    [Arguments("an-absurdly-long-non-guid-id-that-exceeds-the-thirty-six-character-contract")]
    public async Task Canonicalize_never_exceeds_36_characters(string raw) =>
        await Assert.That(DshSessionId.Canonicalize(raw).Length).IsLessThanOrEqualTo(36);
}
