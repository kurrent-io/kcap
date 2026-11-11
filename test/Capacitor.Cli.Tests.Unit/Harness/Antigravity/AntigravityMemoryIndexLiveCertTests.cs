using Capacitor.Cli.Tests.Unit.SessionStartMemory;

namespace Capacitor.Cli.Tests.Unit.Harness.Antigravity;

/// <summary>
/// Certify print-mode injection using the process cwd when workspacePaths is empty; a supplied
/// payload path must still take precedence. Use OAuth: ADC authentication can disable plugin hooks.
/// Forbid tools so a model cannot fetch the nonce through MCP and mimic successful injection.
/// The transcript is authoritative; this suite does not certify the GUI app.
/// Tests mutate real process-global config, so exclude concurrency and restore the complete prior
/// state in finally, including absence of the setting.
/// </summary>
public class AntigravityMemoryIndexLiveCertTests {
    const string LiveGateEnvVar = "KCAP_ANTIGRAVITY_MEMORY_LIVE";
    const string VendorLabel    = "antigravity";

    static void Gate() => MemoryIndexLiveCertHarness.SkipUnlessLiveGateReady(
        LiveGateEnvVar,
        "one real `agy` turn per test",
        "`agy` (>= 1.1.9) on PATH, authenticated, with the kcap plugin installed at "
      + "~/.gemini/config/plugins/kcap/hooks.json (kcap plugin install --antigravity)");

    /// <summary>
    /// Runs one non-interactive <c>agy</c> turn.
    ///
    /// <para>No <c>--conversation</c>: every cert run must start a brand-new conversation, or a
    /// prior turn's context could carry the nonce and the assertion would prove nothing.
    /// <c>--disable-slash-commands</c> keeps skill/slash expansion (agy 1.1.9+) from perturbing the
    /// reply and failing the nonce assertion for a reason unrelated to what this cert
    /// certifies.</para>
    ///
    /// <para><see cref="MemoryIndexLiveCertHarness.ResolveOnPath(string)"/> is called explicitly rather than
    /// left to <c>RunProcessAsync</c>'s own internal resolution, because <c>Process.Start</c> tries
    /// a bare filename against the working directory FIRST — and the working directory here is a
    /// throwaway cert worktree, not this assembly's output folder, so a shadowing <c>agy</c> could
    /// in principle sit there too. Resolving up front pins the exact binary this cert reports on.</para>
    /// </summary>
    static Task<(int ExitCode, string Stdout, string Stderr)> RunAgyAsync(string cwd, string prompt) =>
        MemoryIndexLiveCertHarness.RunProcessAsync(
            MemoryIndexLiveCertHarness.ResolveOnPath("agy"),
            ["-p", prompt, "--output-format", "stream-json", "--disable-slash-commands"],
            cwd);

    [Test, NotInParallel]
    public async Task Nonce_saved_as_a_memory_is_reproduced_by_a_real_agy_turn() {
        Gate();

        // Read BEFORE anything is created: a leaked `true` from an earlier failed run would make
        // this positive case pass vacuously, so the opt-out state is asserted rather than assumed.
        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();
        await Assert.That(original is true).IsFalse();

        var nonce    = MemoryIndexLiveCertHarness.NewNonce();
        using var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await SaveNonceOrCleanUpAsync(nonce, worktree);

        try {
            // Records the exact binary version. A cert passing against an unknown build is how the
            // earlier memory-cert failures were misdiagnosed as code defects when the real cause
            // was a stale installed binary.
            await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "agy", ["--version"]);

            var (exitCode, stdout, _) = await RunAgyAsync(worktree.Path, MemoryIndexLiveCertHarness.PositivePrompt);

            LogInjectedStepEvidence(nonce, stdout);

            // A failure here on a kcap carrying the workspace fallback is REAL — but check auth
            // first: under AGY_ADC_AUTH=1 agy runs no plugin hooks at all (measured 1.1.11), which
            // reproduces the failure for a reason unrelated to what this cert certifies.
            Console.WriteLine(
                "[cert] NOTE: this cert requires the OAuth login. With AGY_ADC_AUTH=1 agy disables "
              + "plugin hooks outright — no capture, no injection — and this assertion fails for an "
              + "auth-mode reason, not an injection defect.");

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(MemoryIndexLiveCertHarness.ExtractAssistantAnswer(stdout)).Contains(nonce);
        } finally {
            await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
        }
    }

    [Test, NotInParallel]
    public async Task Disabled_memory_index_does_not_leak_the_nonce_to_a_real_agy_turn() {
        Gate();

        await MemoryIndexLiveCertHarness.RecordCertEnvironmentAsync(VendorLabel, "agy", ["--version"]);

        // Snapshot before anything is created — this throws on an unreadable config, and doing it
        // after the save would strand a real memory outside the archive-protecting try below.
        var original = await MemoryIndexLiveCertHarness.ReadDisableMemoryIndexAsync();

        var nonce    = MemoryIndexLiveCertHarness.NewNonce();
        using var worktree = MemoryIndexLiveCertHarness.NewCertWorktree(VendorLabel);
        var memoryId = await SaveNonceOrCleanUpAsync(nonce, worktree);

        try {
            await MemoryIndexLiveCertHarness.SetDisableMemoryIndexAsync(true);

            var (exitCode, stdout, _) = await RunAgyAsync(worktree.Path, MemoryIndexLiveCertHarness.NegativePrompt);

            LogInjectedStepEvidence(nonce, stdout);

            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(MemoryIndexLiveCertHarness.ExtractAssistantAnswer(stdout)).DoesNotContain(nonce);
        } finally {
            // Nested: the restore THROWS on a failed or unconfirmed write, and that must not skip
            // the archive — a leaked nonce corrupts every later cert's injected index.
            try {
                await MemoryIndexLiveCertHarness.RestoreDisableMemoryIndexAsync(original);
            } finally {
                await MemoryIndexLiveCertHarness.ArchiveMemoryAsync(VendorLabel, memoryId);
            }
        }
    }

    /// <summary>
    /// Diagnostic ONLY — logged unconditionally, on pass and fail alike, and never asserted on. It
    /// answers a narrower question than the cert itself: did the nonce reach the transcript at all
    /// (a coarse raw-stdout substring check — agy's stream-json shape for an injected
    /// <c>userMessage</c> step has not been pinned by any cert, so this does not parse for a specific
    /// field). Asserting on it would weaken this cert to "the harness ingested it", which is exactly
    /// the standard that let the three earlier adapters merge without proof the MODEL received
    /// anything. The sole pass condition remains the nonce appearing in the model's own answer.
    /// </summary>
    static void LogInjectedStepEvidence(string nonce, string stdout) =>
        Console.WriteLine(
            $"[cert] stdout carried the nonce (proxy for 'userMessage step injected'): {stdout.Contains(nonce)}");

    /// <summary>Saves the nonce memory, deleting the worktree if the save throws — the memory is the
    /// expensive thing to leak, but a save failure must not also strand a temp directory.</summary>
    static async Task<string> SaveNonceOrCleanUpAsync(string nonce, TempDir worktree) {
        try {
            return await MemoryIndexLiveCertHarness.SaveNonceMemoryAsync(VendorLabel, nonce);
        } catch {
            worktree.Dispose();
            throw;
        }
    }
}
