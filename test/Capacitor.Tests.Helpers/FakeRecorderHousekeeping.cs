using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Recording;

namespace Capacitor.Tests.Helpers;

/// <summary>Housekeeping that has nothing to do between drains.</summary>
public sealed class FakeRecorderHousekeeping : IRecorderHousekeeping {
    public int Prepared { get; private set; }

    public int Connected { get; private set; }

    public IReadOnlyCollection<string> SpawnedChildKeys { get; set; } = [];

    /// <summary>Awaited inside <see cref="BeforeDrainAsync"/>, which ignores its token as repository detection
    /// does, so a test can hold the recorder in a call a stop can't interrupt.</summary>
    public Func<Task>? BeforeDrain { get; set; }

    /// <summary>Awaited inside <see cref="PrepareAsync"/>, which ignores its token, so a test can hold a recorder
    /// before it has connected.</summary>
    public Func<Task>? OnPrepare { get; set; }

    public Task PrepareAsync(WatchState state, CancellationToken ct) {
        Prepared++;

        return OnPrepare?.Invoke() ?? Task.CompletedTask;
    }

    public void OnConnected() => Connected++;

    public void RefreshTranscript(string transcriptPath) { }

    public Task BeforeDrainAsync(WatchState state, CancellationToken ct) => BeforeDrain?.Invoke() ?? Task.CompletedTask;

    public Task AfterDrainAsync(WatchState state, string transcriptPath, IReadOnlyList<string> drained, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Awaited inside <see cref="AfterFinalDrainAsync"/>, so a test can make the final drain fail.</summary>
    public Func<Task>? OnAfterFinalDrain { get; set; }

    public Task AfterFinalDrainAsync(WatchState state, IReadOnlyList<string> drained, long shutdownStarted, CancellationToken ct) => OnAfterFinalDrain?.Invoke() ?? Task.CompletedTask;

    public IReadOnlyList<(long Idx, string Line)> ReadAntigravityUsage(string dbPath, long afterIdx, string? createdAt) => [];

    /// <summary>Replaces <see cref="GenerateTitleAsync"/>, so a test can hold a title in progress.</summary>
    public Func<string, Task<ClaudeCliResult?>>? OnGenerateTitle { get; set; }

    public Task<ClaudeCliResult?> GenerateTitleAsync(string userText, string? assistantText, CancellationToken ct) =>
        OnGenerateTitle?.Invoke(userText) ?? Task.FromResult<ClaudeCliResult?>(null);

    public Action? OnHandOffTitle { get; set; }

    public void HandOffTitle(string transcriptPath) => OnHandOffTitle?.Invoke();
}
