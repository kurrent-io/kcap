using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

[ParallelLimiter<SubprocessLimit>]
public class CodexHookTrustProbeTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    const string Initialized = """{"jsonrpc":"2.0","id":1,"result":{"userAgent":"stub"}}""";

    static readonly string[] Critical = ["sessionStart", "stop", "permissionRequest"];

    string Log => Tmp.PathTo("requests.log");

    CodexPaths Paths => new(Home, null);

    /// <summary>A stub app-server; <paramref name="hooksList"/> is the shell run for a <c>hooks/list</c> line, with
    /// the request's id in <c>$id</c>.</summary>
    CodexHookTrustProbe Probe(string hooksList, TimeSpan? deadline = null) {
        Tmp.CreateExecutable("bin/codex", $$"""
            #!/bin/sh
            while IFS= read -r line; do
              printf '%s\n' "$line" >> '{{Log}}'
              case "$line" in
                *'"method":"initialize"'*) printf '%s\n' '{{Initialized}}' ;;
                *'"method":"hooks/list"'*)
                  id=${line#*'"id":'}; id=${id%%,*}
                  {{hooksList}} ;;
              esac
            done
            """);

        return new CodexHookTrustProbe(TestHarnesses.Under(Home, BinaryProbe.Searching(Tmp.PathTo("bin"))), Home, TimeProvider.System) {
            Deadline = deadline ?? TimeSpan.FromSeconds(10),
        };
    }

    static string Answer(string rest) => $"printf '%s\\n' \"{{\\\"jsonrpc\\\":\\\"2.0\\\",\\\"id\\\":$id,{rest.Replace("\"", "\\\"")}}}\"";

    internal static string HooksResult(string status) =>
        Answer($$"""
            "result":{"data":[{"cwd":"/x","hooks":[{{string.Join(",", Critical.Select(e => $$"""{"key":"k:{{e}}","eventName":"{{e}}","command":"kcap hook --codex","currentHash":"sha256:abc","trustStatus":"{{status}}"}"""))}}],"warnings":[],"errors":[]}]}
            """.Trim());

    [Test]
    public async Task Trusted_hooks_read_trusted() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");

        await Assert.That(await Probe(HooksResult("trusted")).ProbeAsync(Paths, CancellationToken.None)).IsEqualTo(CodexHooksTrust.Trusted);
        await Assert.That(File.ReadAllText(Log)).Contains("\"method\":\"initialize\"").And.Contains("\"method\":\"hooks/list\"");
    }

    /// <summary>The <c>codex exec</c> case: hooks installed, never trusted, so Codex skips every one.</summary>
    [Test]
    public async Task Untrusted_hooks_read_untrusted() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");

        await Assert.That(await Probe(HooksResult("untrusted")).ProbeAsync(Paths, CancellationToken.None)).IsEqualTo(CodexHooksTrust.Untrusted);
    }

    /// <summary>A Codex too old for <c>hooks/list</c> gives no answer, which must not read as untrusted.</summary>
    [Test]
    public async Task An_unsupported_hooks_list_is_unknown() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        var probe = Probe(Answer("""
            "error":{"code":-32601,"message":"Method not found"}
            """.Trim()));

        await Assert.That(await probe.ProbeAsync(Paths, CancellationToken.None)).IsEqualTo(CodexHooksTrust.Unknown);
    }

    [Test]
    public async Task No_answer_within_the_deadline_is_unknown() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");

        var trust = await Probe(":", TimeSpan.FromMilliseconds(500)).ProbeAsync(Paths, CancellationToken.None);

        await Assert.That(trust).IsEqualTo(CodexHooksTrust.Unknown);
    }

    [Test]
    public async Task A_file_edited_during_the_ask_is_unknown() {
        Skip.When(OperatingSystem.IsWindows(), "the stub codex is a POSIX shell script");
        var config = Home.CreateFile(".codex/config.toml", "model = \"x\"\n");

        var trust = await Probe($"printf 'edit\\n' >> '{config}'; " + HooksResult("trusted")).ProbeAsync(Paths, CancellationToken.None);

        await Assert.That(trust).IsEqualTo(CodexHooksTrust.Unknown);
    }

    [Test]
    public async Task Missing_codex_is_unknown() {
        var probe = new CodexHookTrustProbe(TestHarnesses.Under(Home), Home, TimeProvider.System);

        await Assert.That(await probe.ProbeAsync(Paths, CancellationToken.None)).IsEqualTo(CodexHooksTrust.Unknown);
    }
}
