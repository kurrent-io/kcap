using System.Reactive.Threading.Tasks;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using AppUnderTest = Capacitor.App.App;

namespace Capacitor.App.Tests.Unit.ViewModels.Onboarding;

[NotInParallel("AvaloniaSession")]
public class HarnessPreflightTests {
    static Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> Detecting(
            params (HarnessId Id, DetectedAgent Agent)[] found) =>
        _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(found.ToDictionary(f => f.Id, f => f.Agent));

    static OnboardingViewModel Wizard(HarnessPreflight preflight) =>
        new([new FakeWizardStep(WizardStepId.Edition), new FakeWizardStep(WizardStepId.Done)], preflight: preflight);

    [Test]
    public async Task No_harness_stops_setup_before_its_first_step() {
        var (blocked, stepVisible, footer, canNext) = await AvaloniaSession.DispatchAsync(async () => {
            var vm = Wizard(new HarnessPreflight(Detecting()));
            await vm.PendingPreflightForTesting;

            return (vm.PreflightBlocked, vm.StepVisible, vm.FooterVisible, vm.CanGoNext);
        });

        await Assert.That(blocked).IsTrue();
        await Assert.That(stepVisible).IsFalse();
        await Assert.That(footer).IsFalse();
        await Assert.That(canNext).IsFalse();
    }

    /// A machine where kcap is installed before the harness is not left waiting on a relaunch.
    [Test]
    public async Task Checking_again_after_installing_enters_setup() {
        var (blockedFirst, blockedThen, stepVisible, enters) = await AvaloniaSession.DispatchAsync(async () => {
            var calls = 0;
            var first = new FakeWizardStep(WizardStepId.Edition);
            var vm = new OnboardingViewModel([first, new FakeWizardStep(WizardStepId.Done)], preflight: new HarnessPreflight(_ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(
                ++calls == 1
                    ? new Dictionary<HarnessId, DetectedAgent>()
                    : new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Copilot] = new(false, true) })));
            await vm.PendingPreflightForTesting;
            var before = vm.PreflightBlocked;
            await vm.CheckAgainCommand.Execute().ToTask();
            await vm.PendingEnterForTesting;

            return (before, vm.PreflightBlocked, vm.StepVisible, first.EnterCount);
        });

        await Assert.That(blockedFirst).IsTrue();
        await Assert.That(blockedThen).IsFalse();
        await Assert.That(stepVisible).IsTrue();
        await Assert.That(enters).IsEqualTo(1);
    }

    [Test]
    public async Task Checking_again_and_finding_nothing_stays_on_the_card() {
        var (blocked, enters) = await AvaloniaSession.DispatchAsync(async () => {
            var first = new FakeWizardStep(WizardStepId.Edition);
            var vm = new OnboardingViewModel([first, new FakeWizardStep(WizardStepId.Done)], preflight: new HarnessPreflight(Detecting()));
            await vm.PendingPreflightForTesting;
            await vm.CheckAgainCommand.Execute().ToTask();

            return (vm.PreflightBlocked, first.EnterCount);
        });

        await Assert.That(blocked).IsTrue();
        await Assert.That(enters).IsEqualTo(0);
    }

    /// Setup itself needs no harness: whoever installs kcap for someone else can finish it, and the harness
    /// is connected later from settings.
    [Test]
    public async Task Continuing_without_a_harness_enters_setup() {
        var (stepVisible, footer, enters) = await AvaloniaSession.DispatchAsync(async () => {
            var first = new FakeWizardStep(WizardStepId.Edition);
            var vm = new OnboardingViewModel([first, new FakeWizardStep(WizardStepId.Done)], preflight: new HarnessPreflight(Detecting()));
            await vm.PendingPreflightForTesting;
            await vm.ContinueWithoutHarnessCommand.Execute().ToTask();
            await vm.PendingEnterForTesting;

            return (vm.StepVisible, vm.FooterVisible, first.EnterCount);
        });

        await Assert.That(stepVisible).IsTrue();
        await Assert.That(footer).IsTrue();
        await Assert.That(enters).IsEqualTo(1);
    }

    /// The Claude and Codex desktop apps keep their CLIs off PATH; the app alone is a harness.
    [Test]
    public async Task A_desktop_app_alone_passes() {
        var (blocked, stepVisible) = await AvaloniaSession.DispatchAsync(async () => {
            var vm = Wizard(new HarnessPreflight(Detecting((HarnessId.Codex, new DetectedAgent(false, false, DesktopAppFound: true)))));
            await vm.PendingPreflightForTesting;

            return (vm.PreflightBlocked, vm.StepVisible);
        });

        await Assert.That(blocked).IsFalse();
        await Assert.That(stepVisible).IsTrue();
    }

    [Test]
    public async Task A_failed_detection_lets_setup_continue() {
        var state = await AvaloniaSession.DispatchAsync(async () => {
            var preflight = new HarnessPreflight(_ => throw new IOException("login shell went away"));
            var vm = Wizard(preflight);
            await vm.PendingPreflightForTesting;

            return preflight.State;
        });

        await Assert.That(state).IsEqualTo(HarnessPreflightState.Passed);
    }

    [Test]
    public async Task The_wizard_waits_while_detection_runs() {
        var (checkingBefore, canNextBefore, stepVisibleAfter) = await AvaloniaSession.DispatchAsync(async () => {
            var gate = new TaskCompletionSource<IReadOnlyDictionary<HarnessId, DetectedAgent>>();
            var vm = Wizard(new HarnessPreflight(_ => gate.Task));
            var before = (vm.PreflightChecking, vm.CanGoNext);
            gate.SetResult(new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Claude] = new(true, false) });
            await vm.PendingPreflightForTesting;

            return (before.PreflightChecking, before.CanGoNext, vm.StepVisible);
        });

        await Assert.That(checkingBefore).IsTrue();
        await Assert.That(canNextBefore).IsFalse();
        await Assert.That(stepVisibleAfter).IsTrue();
    }

    /// A step's entry can start work, such as a resumed install, so none is entered until the pre-flight passes.
    [Test]
    public async Task No_step_is_entered_or_left_before_the_preflight_passes() {
        var (entersBefore, movedBefore, entersAfter) = await AvaloniaSession.DispatchAsync(async () => {
            var gate = new TaskCompletionSource<IReadOnlyDictionary<HarnessId, DetectedAgent>>();
            var first = new FakeWizardStep(WizardStepId.Edition);
            var vm = new OnboardingViewModel([first, new FakeWizardStep(WizardStepId.Done)],
                preflight: new HarnessPreflight(_ => gate.Task));
            var moved = vm.TryGoTo(WizardStepId.Done);
            var before = first.EnterCount;
            gate.SetResult(new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Claude] = new(true, false) });
            await vm.PendingEnterForTesting;

            return (before, moved, first.EnterCount);
        });

        await Assert.That(entersBefore).IsEqualTo(0);
        await Assert.That(movedBefore).IsFalse();
        await Assert.That(entersAfter).IsEqualTo(1);
    }

    /// Quit leaves the app even where setup could otherwise be deferred and the window is still up.
    [Test]
    public async Task Quit_closes_setup_and_the_app() {
        var (closed, quits) = await AvaloniaSession.DispatchAsync(async () => {
            var vm = Wizard(new HarnessPreflight(Detecting()));
            await vm.PendingPreflightForTesting;
            await vm.QuitCommand.Execute().ToTask();

            return (vm.Closed, AppUnderTest.QuitsAfterWizardClose(vm, windowClosed: false));
        });

        await Assert.That(closed).IsTrue();
        await Assert.That(quits).IsTrue();
    }
}
