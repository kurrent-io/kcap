using Capacitor.Cli.Tests.Unit.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.Harness.Kiro;

/// <summary>
/// Certify that Kiro receives the injected memory index, but do not infer once-per-session dedupe
/// from two CLI resume invocations: hook session_id changes even when the chat SessionId stays fixed.
/// A real dedupe certification needs multiple prompts in one interactive process; foundation tests
/// cover the lease separately. These tests mutate real process-global config and require exclusion.
/// </summary>
public class KiroMemoryIndexLiveCertTests {
    const string LiveGateEnvVar = "KCAP_KIRO_MEMORY_LIVE";
    const string VendorLabel    = "kiro";

    static void Gate() => MemoryIndexLiveCertHarness.SkipUnlessLiveGateReady(
        LiveGateEnvVar,
        "one real `kiro-cli chat` turn per test",
        "`kiro-cli` on PATH with its agentSpawn hook wired to `kcap` in ~/.kiro/agents/kcap.json");

    [Test, NotInParallel]
    public async Task Nonce_saved_as_a_memory_is_reproduced_by_a_real_kiro_agent_spawn() {
        Gate();
        var nonce = MemoryIndexLiveCertHarness.NewNonce();

        // Worktree first: it is local and can throw (permissions, disk, temp state), and creating it
        // after the remote save would strand a real memory outside the archive-protecting try.
        using var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);

        try {
            await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "kiro-cli", ["--version"]);

            var answer = await RunKiroAsync(worktree.Path, MemoryIndexLiveCertHarness.PositivePrompt);

            await Assert.That(answer).Contains(nonce);
        } finally {
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
        }
    }

    [Test, NotInParallel]
    public async Task Disabled_memory_index_does_not_leak_the_nonce_to_a_real_kiro_agent_spawn() {
        Gate();

        // Recorded here too: a stale PATH kcap makes a negative control pass vacuously.
        await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "kiro-cli", ["--version"]);

        // Read BEFORE anything is created. This throws on an unreadable config, and doing it after the
        // save would strand a real memory outside the archive-protecting try below.
        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();

        var nonce = MemoryIndexLiveCertHarness.NewNonce();

        // Worktree before the remote save, for the same reason as the positive cert above.
        using var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);

        try {
            await MemoryIndexLiveCertHarness.SetDisableMemoryIndexAsync(true);

            var answer = await RunKiroAsync(worktree.Path, MemoryIndexLiveCertHarness.NegativePrompt);

            await Assert.That(answer).DoesNotContain(nonce);
        } finally {
            // Nested: the restore THROWS on a failed or unconfirmed write, and that must not
            // be allowed to skip the archive — a leaked nonce corrupts every later cert's index.
            try {
                await MemoryIndexLiveCertHarness.RestoreDisableMemoryIndexAsync(original);
            } finally {
                await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
            }
        }
    }

    /// <summary>
    /// Runs one non-interactive Kiro turn. <c>--trust-tools=</c> trusts NO tools: the prompt is
    /// answerable from injected context alone, and an untrusted tool request in
    /// <c>--no-interactive</c> mode would otherwise be the likeliest way for a cert to stall.
    /// </summary>
    static async Task<string> RunKiroAsync(string cwd, string prompt) {
        var (exitCode, stdout, stderr) = await MemoryIndexLiveCertHarness.RunProcessAsync(
            "kiro-cli", ["chat", "--no-interactive", "--trust-tools=", prompt], cwd);

        await Console.Out.WriteLineAsync(
            $"[{VendorLabel}-memory-live] kiro-cli exit={exitCode} stderr={stderr}");
        await Assert.That(exitCode).IsEqualTo(0);

        return MemoryIndexLiveCertHarness.ExtractAssistantAnswer(stdout);
    }
}
