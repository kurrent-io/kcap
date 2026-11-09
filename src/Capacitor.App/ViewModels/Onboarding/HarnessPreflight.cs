using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// A machine with no harness is told so before setup's first step, and can check again after installing
/// one or carry on without it: kcap is sometimes installed before the harness, by someone else. Only a
/// completed detection that found nothing stops here: a failed one passes, and the Harnesses step reports
/// it with its own retry.
public sealed class HarnessPreflight : ReactiveObject {
    public const string Title = "No harness found yet";

    public static string Message { get; } =
        $"kcap stores the sessions of coding-agent harnesses such as Claude Code and Codex, and found none on " +
        $"this {(OperatingSystem.IsMacOS() ? "Mac" : "computer")}. Install one and check again. For Copilot in VS Code, open " +
        $"VS Code once first. You can also continue now and connect one later in {SetupLocation.HarnessesSettings}.";

    readonly Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> _detect;
    HarnessPreflightState _state;

    public HarnessPreflight(Func<CancellationToken, Task<IReadOnlyDictionary<HarnessId, DetectedAgent>>> detect) =>
        _detect = detect;

    public HarnessPreflightState State {
        get => _state;
        private set => this.RaiseAndSetIfChanged(ref _state, value);
    }

    public void ContinueWithoutHarness() => State = HarnessPreflightState.Passed;

    public async Task RunAsync(CancellationToken ct) {
        State = HarnessPreflightState.Checking;

        try {
            var found = await _detect(ct).ConfigureAwait(true);
            State = found.Values.Any(d => d.Detected) ? HarnessPreflightState.Passed : HarnessPreflightState.NoneFound;
        } catch (OperationCanceledException) {
            // Shutdown: there is no setup left to gate.
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: harness pre-flight failed, continuing setup: {ex.Message}");
            State = HarnessPreflightState.Passed;
        }
    }
}
