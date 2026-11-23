using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// The account card's pill and the Accounts summary read one rule, so a signed-out account never shows as recording.
public class AccountSettingsRowTests {
    static readonly string Recording = RecordingStates.Token(RecordingState.Recording);

    static AccountSettingsRow Row(string? signIn) =>
        new("a1", "claude", "~/.claude", ".claude", Recording, null, null, null, new AccountUsageSlot(), IsDefault: true, SignIn: signIn);

    [Test]
    public async Task A_signed_out_account_reads_needs_sign_in_on_its_card_as_in_the_summary() {
        var row = Row(SignInStates.SignedOut);
        await Assert.That(row.StateLabel).IsEqualTo(AccountUsage.NeedsFix(SignInStates.SignedOut, "claude", Recording));
        await Assert.That(row.StateIsAttention).IsTrue();
        await Assert.That(row.StateIsHealthy).IsFalse();
    }

    [Test]
    public async Task Signed_out_Codex_shows_only_the_sign_in_guidance() {
        var row = Row(SignInStates.SignedOut) with {
            Vendor = "codex", RecordingState = RecordingStates.Token(RecordingState.Installed),
        };
        await Assert.That(row.StateLabel).IsEqualTo(AccountUsage.NeedsSignIn);
        await Assert.That(row.StateCommand).IsNull();
        await Assert.That((row with { SignIn = SignInStates.SignedIn }).StateCommand).IsNotNull();
    }

    [Test]
    public async Task A_signed_in_recording_account_reads_healthy() {
        var row = Row(SignInStates.SignedIn);
        await Assert.That(AccountUsage.NeedsFix(SignInStates.SignedIn, "claude", Recording)).IsNull();
        await Assert.That(row.StateIsAttention).IsFalse();
        await Assert.That(row.StateIsHealthy).IsTrue();
    }
}
