using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Recording;

namespace Capacitor.Tests.Helpers;

/// <summary>A host with no housekeeping to do between drains.</summary>
public sealed class FakeRecorderVendor : IRecorderVendor {
    public int Prepared { get; private set; }

    public int Connected { get; private set; }

    public Task PrepareAsync(WatchState state, CancellationToken ct) {
        Prepared++;

        return Task.CompletedTask;
    }

    public void OnConnected() => Connected++;

    public void RefreshTranscript(string transcriptPath) { }

    public Task BeforeDrainAsync(WatchState state, CancellationToken ct) => Task.CompletedTask;

    public Task AfterDrainAsync(WatchState state, string transcriptPath, IReadOnlyList<string> drained, CancellationToken ct) => Task.CompletedTask;

    public Task AfterFinalDrainAsync(WatchState state, IReadOnlyList<string> drained, long shutdownStarted, CancellationToken ct) => Task.CompletedTask;

    public IReadOnlyList<(long Idx, string Line)> ReadAntigravityUsage(string dbPath, long afterIdx, string? createdAt) => [];

    public Task<ClaudeCliResult?> GenerateTitleAsync(string userText, string? assistantText, CancellationToken ct) => Task.FromResult<ClaudeCliResult?>(null);

    public void HandOffTitle(string transcriptPath) { }
}
