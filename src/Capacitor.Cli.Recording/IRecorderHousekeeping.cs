using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Recording;

/// <summary>What a recorder calls between drains that is not itself recording: repository and pull request
/// detection, harness titles and subagent discovery. <see cref="RecorderHousekeeping"/> is the implementation.</summary>
public interface IRecorderHousekeeping {
    /// <summary>The child sources this recorder started, which its host has to stop when the recorder is done.</summary>
    IReadOnlyCollection<string> SpawnedChildKeys { get; }

    /// <summary>Fills in <paramref name="state"/> before the recorder connects.</summary>
    Task PrepareAsync(WatchState state, CancellationToken ct);

    /// <summary>The source is registered and the recorder's final drain will run from here on.</summary>
    void OnConnected();

    /// <summary>Called before every drain, the final one included, for a vendor whose transcript is a copy that
    /// has to be brought up to date.</summary>
    void RefreshTranscript(string transcriptPath);

    Task BeforeDrainAsync(WatchState state, CancellationToken ct);

    Task AfterDrainAsync(WatchState state, string transcriptPath, IReadOnlyList<string> drained, CancellationToken ct);

    /// <param name="shutdownStarted">The <see cref="TimeProvider.GetTimestamp"/> a stop was requested at, which
    /// any time-boxed work here is measured from.</param>
    Task AfterFinalDrainAsync(WatchState state, IReadOnlyList<string> drained, long shutdownStarted, CancellationToken ct);

    /// <summary>Usage rows of an Antigravity conversation database after row <paramref name="afterIdx"/>.</summary>
    IReadOnlyList<(long Idx, string Line)> ReadAntigravityUsage(string dbPath, long afterIdx, string? createdAt);

    /// <summary>Generates a session title, or null when no usable one came back.</summary>
    Task<ClaudeCliResult?> GenerateTitleAsync(string userText, string? assistantText, CancellationToken ct);

    /// <summary>A generated title can't go out over this connection, so something outliving the recorder has
    /// to finish it.</summary>
    void HandOffTitle(string transcriptPath);
}
