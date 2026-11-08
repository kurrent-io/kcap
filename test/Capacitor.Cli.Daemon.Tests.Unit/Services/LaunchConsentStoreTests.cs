using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class LaunchConsentStoreTests {
    [Test]
    public async Task Missing_file_yields_upgrade_safe_policy() {
        using var dir = new TempDir();
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance, TimeProvider.System);
        await Assert.That(store.Current.Default).IsEqualTo(LaunchConsentDefault.Allow);
        await Assert.That(store.Current.PromptTimeoutSeconds).IsEqualTo(45);
        await Assert.That(store.Current.Rules.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Corrupt_file_yields_upgrade_safe_policy() {
        using var dir = new TempDir();
        dir.CreateFile("consent.json", "{not json");
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance, TimeProvider.System);
        await Assert.That(store.Current.Default).IsEqualTo(LaunchConsentDefault.Allow);
    }

    [Test]
    public async Task Replace_persists_and_reloads() {
        using var dir = new TempDir();
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance, TimeProvider.System);
        var next = new LaunchConsentPolicy(LaunchConsentDefault.Prompt, 60,
            [new LaunchConsentRule("deny", "user_x", "review-flow", null, "codex")]);
        var ok = store.TryReplace(next, out var error);
        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();

        var reloaded = new LaunchConsentStore(dir.Path, NullLogger.Instance, TimeProvider.System);
        await Assert.That(reloaded.Current.Default).IsEqualTo(LaunchConsentDefault.Prompt);
        await Assert.That(reloaded.Current.PromptTimeoutSeconds).IsEqualTo(60);
        await Assert.That(reloaded.Current.Rules[0].Requester).IsEqualTo("user_x");
    }

    [Test]
    public async Task Replace_rejects_invalid_action_and_kind() {
        using var dir = new TempDir();
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance, TimeProvider.System);
        var badAction = new LaunchConsentPolicy(LaunchConsentDefault.Allow, 45,
            [new LaunchConsentRule("maybe", null, null, null, null)]);
        await Assert.That(store.TryReplace(badAction, out var e1)).IsFalse();
        await Assert.That(e1).Contains("action");

        var badKind = new LaunchConsentPolicy(LaunchConsentDefault.Allow, 45,
            [new LaunchConsentRule("allow", null, "flows", null, null)]);
        await Assert.That(store.TryReplace(badKind, out var e2)).IsFalse();
        await Assert.That(e2).Contains("kind");
    }

    [Test]
    public async Task Replace_clamps_prompt_timeout() {
        using var dir = new TempDir();
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance, TimeProvider.System);
        await Assert.That(store.TryReplace(
            new LaunchConsentPolicy(LaunchConsentDefault.Allow, 1, []), out _)).IsTrue();
        await Assert.That(store.Current.PromptTimeoutSeconds).IsEqualTo(5);
        await Assert.That(store.TryReplace(
            new LaunchConsentPolicy(LaunchConsentDefault.Allow, 9999, []), out _)).IsTrue();
        await Assert.That(store.Current.PromptTimeoutSeconds).IsEqualTo(300);
    }

    [Test]
    public async Task Replace_writes_an_owner_only_consent_file_in_an_owner_only_directory() {
        // consent.json carries requester ids and repo paths, so it must not be world/group
        // readable. Unix-only: file modes are a no-op on Windows.
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance, TimeProvider.System);
        var ok = store.TryReplace(new LaunchConsentPolicy(LaunchConsentDefault.Allow, 45, []), out _);
        await Assert.That(ok).IsTrue();

        const UnixFileMode ownerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        const UnixFileMode ownerOnlyDir  = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        var consentFile = dir.PathTo("consent.json");
        await Assert.That(File.GetUnixFileMode(consentFile)).IsEqualTo(ownerOnlyFile);
        await Assert.That(File.GetUnixFileMode(dir.Path)).IsEqualTo(ownerOnlyDir);
    }

    /// <summary>The switch fails closed while launch consent keeps degrading as it always has: a bad steering value
    /// must not make the rest of the document unreadable, which would drop a deny to allow.</summary>
    [Test]
    [Arguments(null, "Allow", "Seeded")]
    [Arguments("{not json", "Allow", "Quarantined")]
    [Arguments("""{"default":"deny"}""", "Deny", "Respected")]
    [Arguments("""{"default":"deny","steering":null}""", "Deny", "Respected")]
    [Arguments("""{"default":"deny","steering":"maybe"}""", "Deny", "Respected")]
    [Arguments("""{"default":"deny","steering":"ON"}""", "Deny", "Respected")]
    [Arguments("""{"default":"deny","steering":42}""", "Deny", "Respected")]
    [Arguments("""{"default":"deny","steering":true}""", "Deny", "Respected")]
    [Arguments("""{"default":"deny","steering":{"on":true}}""", "Deny", "Respected")]
    [Arguments("""{"default":"deny","steering":["on"]}""", "Deny", "Respected")]
    public async Task A_missing_or_corrupt_switch_reads_as_off(string? document, string launchConsent, string seed) {
        using var dir = new TempDir();
        if (document is not null) dir.CreateFile("consent.json", document);

        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance, TimeProvider.System);

        await Assert.That(store.SteeringOn).IsFalse();
        await Assert.That(store.Current.Default.ToString()).IsEqualTo(launchConsent);
        await Assert.That(store.BootSeed("prompt").Outcome.ToString()).IsEqualTo(seed);
    }

    [Test]
    public async Task The_switch_persists_on_and_off() {
        using var dir = new TempDir();
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance, TimeProvider.System);

        await Assert.That(store.TrySetSteering(true, out var error)).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(new LaunchConsentStore(dir.Path, NullLogger.Instance, TimeProvider.System).SteeringOn).IsTrue();

        await Assert.That(store.TrySetSteering(false, out _)).IsTrue();
        await Assert.That(new LaunchConsentStore(dir.Path, NullLogger.Instance, TimeProvider.System).SteeringOn).IsFalse();
    }

    /// <summary>Turning steering on is not a launch-consent decision: an operator's policy reads back unchanged, and a
    /// daemon that never had one still looks unchosen to a boot seed.</summary>
    [Test]
    public async Task Setting_the_switch_leaves_launch_consent_as_it_was() {
        using var chosen = new TempDir();
        chosen.CreateFile("consent.json",
            """{"default":"deny","prompt_timeout_seconds":60,"rules":[{"action":"allow","requester":"user_x"}],"default_source":"operator"}""");
        new LaunchConsentStore(chosen.Path, NullLogger.Instance, TimeProvider.System).TrySetSteering(true, out _);

        var reloaded = new LaunchConsentStore(chosen.Path, NullLogger.Instance, TimeProvider.System);
        await Assert.That(reloaded.Current.Default).IsEqualTo(LaunchConsentDefault.Deny);
        await Assert.That(reloaded.Current.PromptTimeoutSeconds).IsEqualTo(60);
        await Assert.That(reloaded.Current.Rules.Single().Requester).IsEqualTo("user_x");
        await Assert.That(reloaded.BootSeed("prompt").Outcome).IsEqualTo(SeedOutcome.Respected);

        using var unchosen = new TempDir();
        new LaunchConsentStore(unchosen.Path, NullLogger.Instance, TimeProvider.System).TrySetSteering(true, out _);

        var fresh = new LaunchConsentStore(unchosen.Path, NullLogger.Instance, TimeProvider.System);
        await Assert.That(fresh.Current.Default).IsEqualTo(LaunchConsentPolicy.UpgradeSafe.Default);
        await Assert.That(fresh.Current.PromptTimeoutSeconds).IsEqualTo(LaunchConsentPolicy.UpgradeSafe.PromptTimeoutSeconds);
        await Assert.That(fresh.Current.Rules).IsEmpty();
        await Assert.That(fresh.BootSeed("prompt").Outcome).IsEqualTo(SeedOutcome.Rewritten);
        await Assert.That(fresh.SteeringOn).IsTrue();
    }

    [Test]
    public async Task Replacing_the_policy_keeps_the_switch() {
        using var dir = new TempDir();
        var store = new LaunchConsentStore(dir.Path, NullLogger.Instance, TimeProvider.System);
        store.TrySetSteering(true, out _);

        await Assert.That(store.TryReplace(new LaunchConsentPolicy(LaunchConsentDefault.Deny, 45, []), out _)).IsTrue();

        await Assert.That(new LaunchConsentStore(dir.Path, NullLogger.Instance, TimeProvider.System).SteeringOn).IsTrue();
    }
}
