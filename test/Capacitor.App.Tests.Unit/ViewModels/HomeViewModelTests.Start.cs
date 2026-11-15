using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class HomeViewModelTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_sends_the_selected_repository_and_harness() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("gemini");
            vm.Goal = "Fix the flaky test";
            await vm.StartCommand.Execute();

            await Assert.That(launch.Last!.RepoPath).IsEqualTo("/repo/a");
            await Assert.That(launch.Last!.Vendor).IsEqualTo("gemini");
            await Assert.That(launch.Last!.Prompt).IsEqualTo("Fix the flaky test");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_carries_the_chosen_model_and_effort_and_a_vendor_change_resets_the_model() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            vm.Goal = "go";

            await vm.SelectRepositoryAsync("/repo/a");
            vm.SelectedModel = "claude-fable-5";
            vm.SelectedEffort = "high";
            await vm.StartCommand.Execute();

            await Assert.That(launch.Last!.Model).IsEqualTo("claude-fable-5");
            await Assert.That(launch.Last!.Effort).IsEqualTo("high");

            // Model ids are vendor-specific; effort's ladder is shared and survives.
            await vm.ChooseHarnessAsync("codex");
            await Assert.That(vm.SelectedModel).IsEqualTo("");
            await Assert.That(vm.SelectedEffort).IsEqualTo("high");

            vm.Goal = "go";
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.Model).IsEqualTo("");
        });
    }

    /// A blank goal disables Start and its tip says why, and Execute() past CanExecute still sends nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_needs_a_prompt() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            await vm.SelectRepositoryAsync("/repo/a");

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(vm.StartButtonTip).IsEqualTo(HomeViewModel.NeedsPromptTip);
            vm.Goal = "  \n ";
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last).IsNull();

            vm.Goal = "go";
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(vm.StartButtonTip).IsEqualTo("Start");
        });
    }

    /// Manual must ride the launch: without the flag Claude falls back to the user's own
    /// <c>permissions.defaultMode</c>, which can be <c>auto</c> while the chip still reads Manual.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Permission_mode_defaults_to_manual_which_is_sent_explicitly() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            vm.Goal = "go";

            await vm.SelectRepositoryAsync("/repo/a");
            await Assert.That(vm.SelectedPermissionMode).IsEqualTo(HomeViewModel.DefaultPermissionMode);

            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.PermissionMode).IsEqualTo(ClaudePermissionModes.Manual);
        });
    }

    /// The mode vocabulary is Claude's, so a choice made under Claude never rides a launch of
    /// another vendor — but it is kept, not reset, so switching back restores it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_chosen_permission_mode_is_sent_for_claude_only() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            vm.Goal = "go";

            await vm.SelectRepositoryAsync("/repo/a");
            vm.SelectedPermissionMode = "bypassPermissions";
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.PermissionMode).IsEqualTo("bypassPermissions");

            await vm.ChooseHarnessAsync("codex");
            vm.Goal = "go";
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.PermissionMode).IsNull();

            await vm.ChooseHarnessAsync("claude");
            vm.Goal = "go";
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.PermissionMode).IsEqualTo("bypassPermissions");
        });
    }

    /// An untouched launcher sends no posture, so a daemon without posture support still accepts it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Codex_posture_defaults_to_the_daemons_own_pair_which_sends_nothing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            vm.Goal = "go";

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.CodexPosture).IsNull();
        });
    }

    /// Changing either half sends both — the daemon rejects a partial posture — and only for codex.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_chosen_codex_posture_is_sent_whole_and_for_codex_only() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            vm.Goal = "go";

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");
            vm.SelectedCodexApproval = "never";
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.CodexPosture).IsEqualTo(new CodexLaunchPosture("workspace-write", "never"));

            vm.SelectedCodexSandbox = "read-only";
            vm.Goal = "go";
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.CodexPosture).IsEqualTo(new CodexLaunchPosture("read-only", "never"));

            await vm.ChooseHarnessAsync("claude");
            vm.Goal = "go";
            await vm.StartCommand.Execute();
            await Assert.That(launch.Last!.CodexPosture).IsNull();
        });
    }

    /// Auto is not a Codex approval token: it launches on-request with Codex's auto-review deciding,
    /// and is sent even on the default sandbox.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Auto_codex_approval_is_sent_as_on_request_with_auto_review() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            vm.Goal = "go";

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.ChooseHarnessAsync("codex");
            vm.SelectedCodexApproval = HostedHarnessCatalog.CodexAutoApproval;
            await vm.StartCommand.Execute();

            await Assert.That(launch.Last!.CodexPosture)
                .IsEqualTo(new CodexLaunchPosture("workspace-write", "on-request", "auto_review"));
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_start_surfaces_the_servers_reason() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var vm = Build(out var launch, out _, path);
            vm.Goal = "go";
            launch.Next = new LaunchOutcome(false, null, "Daemon 'kcap-dev' is at capacity.");

            await vm.SelectRepositoryAsync("/repo/a");
            await vm.StartCommand.Execute();

            await Assert.That(vm.StartError).IsEqualTo("Daemon 'kcap-dev' is at capacity.");
        });
    }
}
