using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// The daemon apply loop, service environment, and affirmation resolution must derive from one
/// registry. An opt-out omitted from service forwarding leaves the operator unable to disable it.
/// </summary>
public class GatedReviewerRegistryTests {
    /// <summary>
    /// Every row has a <see cref="DaemonConfig"/> flag behind it.
    ///
    /// <para>This is the direction the service-unit test structurally cannot see: that one ranges over
    /// the registry and proves each row survives capture, so a row the DAEMON cannot apply is still
    /// green there. Adding a reviewer to the registry publishes its variable into every service unit
    /// and into the affirm verb's usage text; without an accessor the operator sets a documented switch
    /// that does nothing. <c>ConsentApplier</c> throws for that case, and this is what makes CI hit the
    /// throw instead of a customer's first boot.</para>
    /// </summary>
    [Test]
    public async Task Consent_applier_covers_every_gated_reviewer() {
        await Assert.That(GatedReviewers.All).IsNotEmpty()
            .Because("an empty registry would make every assertion here vacuously true");

        foreach (var reviewer in GatedReviewers.All) {
            var config = new DaemonConfig();

            // Resolving is half the assertion: an unmapped vendor throws NotSupportedException.
            DaemonRunner.ConsentApplier(config, reviewer.Vendor)(false);

            // Assert the specific vendor property, not the number of false flags: a fresh config would let
            // two vendors alias one property while still clearing exactly one flag per case.
            var expected = ExpectedFlag(reviewer.Vendor);

            await Assert.That(expected(config)).IsFalse()
                .Because($"{reviewer.Vendor}'s opt-out must clear {reviewer.Vendor}'s own flag, not "
                       + "another vendor's — aliasing would silently make one switch disable the wrong "
                       + "reviewer while leaving the named one running");

            foreach (var (vendor, flag) in AllFlags().Where(f => f.Vendor != reviewer.Vendor))
                await Assert.That(flag(config)).IsTrue()
                    .Because($"{reviewer.Vendor}'s opt-out must not touch {vendor}");
        }
    }

    /// <summary>The flag each vendor's opt-out is expected to clear. Deliberately a SECOND, independent
    /// statement of the mapping — a test that re-derived it from the code under test could not detect a
    /// mis-wiring.</summary>
    static Func<DaemonConfig, bool> ExpectedFlag(string vendor) =>
        AllFlags().Single(f => f.Vendor == vendor).Flag;

    static (string Vendor, Func<DaemonConfig, bool> Flag)[] AllFlags() => [
        ("gemini",      c => c.GeminiUnattendedReviewerEnabled),
        ("kiro",        c => c.KiroUnattendedReviewerEnabled),
        ("opencode",    c => c.OpenCodeUnattendedReviewerEnabled),
        ("antigravity", c => c.AntigravityUnattendedReviewerEnabled),
        ("pi",          c => c.PiUnattendedReviewerEnabled),
        ("grok",        c => c.GrokUnattendedReviewerEnabled),
        ("muse",        c => c.MuseUnattendedReviewerEnabled)
    ];

    /// <summary>An unmapped vendor is refused loudly, not silently no-op'd.</summary>
    [Test]
    public async Task Consent_applier_refuses_a_reviewer_it_cannot_apply() {
        await Assert.That(() => DaemonRunner.ConsentApplier(new DaemonConfig(), "notavendor"))
            .Throws<NotSupportedException>();
    }

    /// <summary>
    /// Every row carries a usable opt-out variable. A null or blank one would land in the service-unit
    /// allowlist as an empty key and quietly drop that reviewer's only lever.
    /// </summary>
    [Test]
    public async Task Every_gated_reviewer_names_an_opt_out_variable() {
        foreach (var reviewer in GatedReviewers.All) {
            await Assert.That(string.IsNullOrWhiteSpace(reviewer.EnableEnvVar)).IsFalse()
                .Because($"{reviewer.Vendor} would otherwise be undisableable");
            await Assert.That(string.IsNullOrWhiteSpace(reviewer.Vendor)).IsFalse();
        }

        await Assert.That(GatedReviewers.All.Select(r => r.EnableEnvVar).Distinct().Count())
            .IsEqualTo(GatedReviewers.All.Length)
            .Because("two reviewers sharing a variable makes one of them undisableable on its own");
    }

}
