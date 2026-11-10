using Capacitor.Cli.Commands;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A run that cannot ask names every answer it is missing, so one retry is enough.</summary>
public class LocalSetupMissingAnswerTests {
    static readonly LocalPlan Install = new(PlanHint.Install, "1.4.1", null);

    static LocalSetupArgs Args(bool singleUser, bool yes) => new(false, singleUser, yes, true);

    [Test]
    public async Task Missing_both_names_both() {
        var missing = LocalSetupTerminal.MissingAnswer(Args(singleUser: false, yes: false), Install, "/root");

        await Assert.That(missing).Contains("--single-user and --yes");
        await Assert.That(missing).Contains("kcap local 1.4.1 in /root");
    }

    [Test]
    public async Task Missing_one_names_that_one() {
        await Assert.That(LocalSetupTerminal.MissingAnswer(Args(singleUser: false, yes: true), Install, "/root"))
            .StartsWith("Pass --single-user to install kcap local 1.4.1 in /root.");
        await Assert.That(LocalSetupTerminal.MissingAnswer(Args(singleUser: true, yes: false), Install, "/root"))
            .IsEqualTo("Pass --yes to install kcap local 1.4.1 in /root.");
        await Assert.That(LocalSetupTerminal.MissingAnswer(Args(singleUser: true, yes: true), Install, "/root")).IsNull();
    }

    /// <summary>The installation a reuse finds was confirmed when it was made, so a rerun repeats neither answer.</summary>
    [Test]
    public async Task A_reuse_misses_nothing() {
        var reuse = new LocalPlan(PlanHint.Reuse, "1.4.1", new("0123456789abcdef0123456789abcdef", 9473, "http://localhost:9473", "1.4.1"));

        await Assert.That(LocalSetupTerminal.MissingAnswer(Args(singleUser: false, yes: false), reuse, "/root")).IsNull();
    }
}
