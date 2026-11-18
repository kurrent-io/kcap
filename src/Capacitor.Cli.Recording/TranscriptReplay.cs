namespace Capacitor.Cli.Recording;

/// <summary>
/// The transcript before a resume cursor, read once for every consumer that rebuilds state a resumed
/// recorder would otherwise never see: it drains only past the server's frontier. Lines before a windowed
/// consumer's window are still read, since there is no cheap seek to line N, but never handed to it.
/// Opened for sharing, because the agent is still writing the file, and fail-open: losing a rebuild beats
/// failing a recorder's startup.
/// </summary>
internal static class TranscriptReplay {
    /// How far back a windowed consumer looks. An unfinished tool sits at the tail, since nothing is written
    /// after its call until the result arrives, so anything older is settled and only costs startup time.
    internal const int WindowLines = 512;

    /// <param name="progress">Called after each line, for a caller that has to show it is getting through them.</param>
    internal static async Task ReplayAsync(
            string transcriptPath, int upToLine, IReadOnlyList<TranscriptReplayConsumer> consumers, CancellationToken ct, Action<string> log,
            Action? progress = null) {
        if (ct.IsCancellationRequested || upToLine <= 0 || consumers.Count == 0) return;

        var windowStart = Math.Max(0, upToLine - WindowLines);
        var done        = new bool[consumers.Count];

        try {
            await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var       reader = new StreamReader(stream);

            for (var lineNumber = 0; lineNumber < upToLine && !Array.TrueForAll(done, finished => finished); lineNumber++) {
                if (await reader.ReadLineAsync(ct) is not { } line) break;

                for (var i = 0; i < consumers.Count; i++) {
                    if (done[i] || (consumers[i].WindowOnly && lineNumber < windowStart)) continue;
                    done[i] = consumers[i].OnLine(line);
                }

                progress?.Invoke();
            }
        } catch (OperationCanceledException) {
            // Shutdown racing startup: expected, and not worth a log line.
        } catch (Exception ex) {
            log($"Transcript replay before the resume cursor skipped: {ex.Message}");
        }
    }
}
