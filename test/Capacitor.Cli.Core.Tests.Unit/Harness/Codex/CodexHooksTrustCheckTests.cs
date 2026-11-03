using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

[ParallelLimiter<SubprocessLimit>]
public class CodexHooksTrustCheckTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    const string KcapHooks = """
        {"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"kcap hook --codex","timeout":30}]}]}}
        """;

    const string RecordedTrust = """
        [hooks.state."user:SessionStart:0"]
        trusted_hash = "sha256:abc"
        """;

    CodexPaths Paths => new(Home, null);

    string Log => Tmp.PathTo("codex.log");

    /// <summary>The registry as the daemon's refresh leaves it: the default home with the answer Codex gave for
    /// the files on disk at that moment.</summary>
    AccountRegistry Answered(string trust) => new() {
        Accounts = [
            new VendorAccount("a", HarnessId.Codex, Paths.Home, ".codex", DateTimeOffset.UnixEpoch,
                HookTrust: trust, HookTrustFingerprint: CodexTrustFingerprint.Of(Paths)),
        ],
    };

    /// <summary>A codex that logs every start and answers <c>hooks/list</c> with <paramref name="status"/>.</summary>
    CodexHookTrustProbe Probe(string status) {
        Tmp.CreateExecutable("bin/codex", $$$"""
            #!/bin/sh
            printf 'started\n' >> '{{{Log}}}'
            while IFS= read -r line; do
              case "$line" in
                *'"method":"initialize"'*) printf '%s\n' '{"jsonrpc":"2.0","id":1,"result":{}}' ;;
                *'"method":"hooks/list"'*)
                  id=${line#*'"id":'}; id=${id%%,*}
                  {{{CodexHookTrustProbeTests.HooksResult(status)}}} ;;
              esac
            done
            """);

        return new CodexHookTrustProbe(TestHarnesses.Under(Home, BinaryProbe.Searching(Tmp.PathTo("bin"))), Home, TimeProvider.System);
    }

    [Test]
    [Arguments(HookTrustStates.Trusted, CodexHooksTrust.Trusted)]
    [Arguments(HookTrustStates.Untrusted, CodexHooksTrust.Untrusted)]
    public async Task A_stored_answer_for_the_files_on_disk_stands(string stored, CodexHooksTrust expected) {
        Home.CreateFile(".codex/hooks.json", KcapHooks);

        await Assert.That(CodexHooksTrustCheck.Stored(Answered(stored), Paths)).IsEqualTo(expected);
    }

    /// <summary>Codex trusts a hash of each hook's definition, so an answer given before either file changed says
    /// nothing about the hooks there now.</summary>
    [Test]
    public async Task A_stored_answer_for_other_bytes_is_unknown() {
        Home.CreateFile(".codex/hooks.json", KcapHooks);
        var registry = Answered(HookTrustStates.Trusted);
        Home.CreateFile(".codex/config.toml", "model = \"x\"\n");

        await Assert.That(CodexHooksTrustCheck.Stored(registry, Paths)).IsEqualTo(CodexHooksTrust.Unknown);
    }

    [Test]
    public async Task No_stored_answer_is_unknown() {
        Home.CreateFile(".codex/hooks.json", KcapHooks);
        var unanswered = new AccountRegistry {
            Accounts = [new VendorAccount("a", HarnessId.Codex, Paths.Home, ".codex", DateTimeOffset.UnixEpoch)],
        };

        await Assert.That(CodexHooksTrustCheck.Stored(unanswered, Paths)).IsEqualTo(CodexHooksTrust.Unknown);
        await Assert.That(CodexHooksTrustCheck.Stored(new AccountRegistry(), Paths)).IsEqualTo(CodexHooksTrust.Unknown);
        await Assert.That(CodexHooksTrustCheck.Stored(null, Paths)).IsEqualTo(CodexHooksTrust.Unknown);
    }

    /// <summary>Another account's answer is for another home's files.</summary>
    [Test]
    public async Task An_answer_for_another_home_is_not_this_ones() {
        Home.CreateFile(".codex/hooks.json", KcapHooks);
        var other = new AccountRegistry {
            Accounts = [
                new VendorAccount("b", HarnessId.Codex, Home.PathTo(".codex-work"), ".codex-work", DateTimeOffset.UnixEpoch,
                    HookTrust: HookTrustStates.Untrusted, HookTrustFingerprint: CodexTrustFingerprint.Of(Paths)),
            ],
        };

        await Assert.That(CodexHooksTrustCheck.Stored(other, Paths)).IsEqualTo(CodexHooksTrust.Unknown);
    }

    [Test]
    public async Task A_current_stored_answer_is_used_without_starting_codex() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        Home.CreateFile(".codex/hooks.json", KcapHooks);
        var probe = Probe("trusted");

        var trust = await CodexHooksTrustCheck.ReadAsync(Answered(HookTrustStates.Untrusted), Paths, probe, CancellationToken.None);

        await Assert.That(trust).IsEqualTo(CodexHooksTrust.Untrusted);
        await Assert.That(File.Exists(Log)).IsFalse();
    }

    /// <summary>Hooks the daemon has not looked at yet, in a home that records some trust, are answered by Codex on
    /// the spot: only Codex can tell whether a recorded hash is for these hooks.</summary>
    [Test]
    public async Task Without_a_current_stored_answer_codex_is_asked() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        Home.CreateFile(".codex/hooks.json", KcapHooks);
        Home.CreateFile(".codex/config.toml", RecordedTrust);

        var trust = await CodexHooksTrustCheck.ReadAsync(new AccountRegistry(), Paths, Probe("untrusted"), CancellationToken.None);

        await Assert.That(trust).IsEqualTo(CodexHooksTrust.Untrusted);
        await Assert.That(File.Exists(Log)).IsTrue();
    }

    /// <summary>Codex keeps each trusted hook as a <c>trusted_hash</c> in <c>config.toml</c>, so a home recording none
    /// trusts none of kcap's hooks, which a Codex too slow to answer cannot change.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("model = \"x\"\n")]
    public async Task A_home_recording_no_trust_is_untrusted_without_asking_codex(string? configToml) {
        Home.CreateFile(".codex/hooks.json", KcapHooks);
        if (configToml is not null) Home.CreateFile(".codex/config.toml", configToml);

        await Assert.That(CodexHooksTrustCheck.Offline(new AccountRegistry(), Paths)).IsEqualTo(CodexHooksTrust.Untrusted);
    }

    /// <summary>A recorded hash may or may not be for kcap's hooks as they are now; that is Codex's to say.</summary>
    [Test]
    public async Task A_home_recording_trust_is_left_to_codex() {
        Home.CreateFile(".codex/hooks.json", KcapHooks);
        Home.CreateFile(".codex/config.toml", RecordedTrust);

        await Assert.That(CodexHooksTrustCheck.Offline(new AccountRegistry(), Paths)).IsEqualTo(CodexHooksTrust.Unknown);
    }

    [Test]
    public async Task Without_kcap_hooks_nothing_is_untrusted() =>
        await Assert.That(CodexHooksTrustCheck.Offline(new AccountRegistry(), Paths)).IsEqualTo(CodexHooksTrust.Unknown);

    [Test]
    public async Task Without_kcap_hooks_there_is_nothing_to_ask() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");

        var trust = await CodexHooksTrustCheck.ReadAsync(new AccountRegistry(), Paths, Probe("untrusted"), CancellationToken.None);

        await Assert.That(trust).IsEqualTo(CodexHooksTrust.Unknown);
        await Assert.That(File.Exists(Log)).IsFalse();
    }
}
