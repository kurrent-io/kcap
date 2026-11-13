using Capacitor.App.Tests.Unit.Services;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Tests.Unit.ViewModels.Onboarding;

/// <summary>The install page when no local bundle is published for this version. The wizard explains it on Windows
/// only, where it passes <c>bundleUnpublished</c>; elsewhere the page passes none and keeps the installer's words.</summary>
public class InstallStepUnpublishedBundleTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string CliMessage = "No local edition is published for kcap 1.3.0 on Windows yet.";

    static readonly LocalEmbeddingModelManifest Offered = new("builtin-test", 1, new('e', 64), [new("model.onnx", new('f', 64), 300L * 1024 * 1024)]);

    LocalInstallController Controller(FakeKcapCli cli) =>
        new(() => cli, LocalSetupScript.NoSetup(Config), () => false, action => action(), TelemetryProbe.Live("setup", Config.Root).Funnel);

    static InstallStepViewModel Step(LocalInstallController controller, Func<bool>? bundleUnpublished) =>
        new(controller, () => "default", () => "default", () => true, _ => Task.FromResult<LocalEmbeddingModelManifest?>(Offered),
            bundleUnpublished: bundleUnpublished);

    static async Task EnterAndInstallAsync(InstallStepViewModel install) {
        await install.OnEnterAsync(CancellationToken.None);
        await install.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
    }

    static FakeKcapCli Fails(string code, string message = CliMessage) =>
        new() { SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed(code, message)) };

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task On_windows_an_unpublished_bundle_offers_the_other_editions_instead_of_a_retry() {
        var cli = Fails("bundle_not_published");

        var (message, title, tryAgain, other, progress, back, asked) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli), () => false);
            var asked = 0;
            install.OtherEditionRequested += () => asked++;
            await EnterAndInstallAsync(install);
            await install.ChooseOtherEditionCommand.Execute().ToTask();

            return (install.FailureMessage, install.Title, install.TryAgainVisible, install.OtherEditionVisible,
                install.ProgressVisible, install.BackVisible, asked);
        });

        await Assert.That(message).IsEqualTo(InstallStepViewModel.NotPublishedText);
        await Assert.That(title).IsEqualTo(InstallStepViewModel.NotPublishedTitle);
        await Assert.That(tryAgain).IsFalse();
        await Assert.That(other).IsTrue();
        await Assert.That(progress).IsFalse();
        await Assert.That(back).IsTrue();
        await Assert.That(asked).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Off_windows_an_unpublished_bundle_keeps_the_installers_own_words() {
        var cli = Fails("bundle_not_published", "No local bundle is published at https://dl.test/osx-arm64/kcap-local-1.3.0-osx-arm64.json.");

        var (message, title, tryAgain, other, progress) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli), bundleUnpublished: null);
            await EnterAndInstallAsync(install);

            return (install.FailureMessage, install.Title, install.TryAgainVisible, install.OtherEditionVisible, install.ProgressVisible);
        });

        await Assert.That(message).IsEqualTo("No local bundle is published at https://dl.test/osx-arm64/kcap-local-1.3.0-osx-arm64.json.");
        await Assert.That(title).IsEqualTo($"Installing kcap on {ThisDevice.Name}");
        await Assert.That(tryAgain).IsFalse();
        await Assert.That(other).IsFalse();
        await Assert.That(progress).IsTrue();
    }

    /// A failed download says nothing about what is published, so it keeps its retry where unpublished bundles are explained.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task On_windows_a_download_failure_keeps_its_retry() {
        var cli = Fails("download_failed", "Could not read https://dl.test/win-x64/kcap-local-1.3.0-win-x64.json: No such host is known.");

        var (tryAgain, other, notPublished) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli), () => false);
            await EnterAndInstallAsync(install);

            return (install.TryAgainVisible, install.OtherEditionVisible, install.NotPublished);
        });

        await Assert.That(tryAgain).IsTrue();
        await Assert.That(other).IsFalse();
        await Assert.That(notPublished).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task On_windows_a_page_reached_once_the_bundle_is_known_unpublished_settles_without_a_run() {
        var cli = new FakeKcapCli();
        var controller = Controller(cli);

        var (choice, message, other) = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(controller, () => true);
            await install.OnEnterAsync(CancellationToken.None);

            return (install.ChoiceVisible, install.FailureMessage, install.OtherEditionVisible);
        });

        await Assert.That(cli.SetupLocalCallCount).IsEqualTo(0);
        await Assert.That(((LocalInstallOutcome.Failed)controller.Outcome!).Code).IsEqualTo("bundle_not_published");
        await Assert.That(choice).IsFalse();
        await Assert.That(message).IsEqualTo(InstallStepViewModel.NotPublishedText);
        await Assert.That(other).IsTrue();
    }

    /// The bundle can be found missing while the search question is up; pressing Install then runs nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task On_windows_install_pressed_after_the_bundle_is_found_unpublished_runs_nothing() {
        var cli = new FakeKcapCli();
        var unpublished = false;

        var message = await AvaloniaSession.DispatchAsync(async () => {
            var install = Step(Controller(cli), () => unpublished);
            await install.OnEnterAsync(CancellationToken.None);
            unpublished = true;
            await install.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            return install.FailureMessage;
        });

        await Assert.That(cli.SetupLocalCallCount).IsEqualTo(0);
        await Assert.That(message).IsEqualTo(InstallStepViewModel.NotPublishedText);
    }
}
