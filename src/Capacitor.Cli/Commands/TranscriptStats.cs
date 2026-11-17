namespace Capacitor.Cli.Commands;

/// <summary>
/// One pass over a JSONL transcript. Indexes are physical line numbers, the numbering the server's watermark
/// uses; <see cref="LastRelevantIndex"/> is the last line the server's normalizer turns into an event.
/// </summary>
internal readonly record struct TranscriptStats(int? LastNonBlankIndex, int? LastRelevantIndex, int NonBlankCount) {
    public static async Task<TranscriptStats> ReadAsync(string transcriptPath, Func<string, bool> isImportRelevantLine, CancellationToken ct) {
        await using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var       reader = new StreamReader(stream);

        int? lastIdx         = null;
        int? lastRelevantIdx = null;
        var  count           = 0;
        var  lineIdx         = 0;

        while (await reader.ReadLineAsync(ct) is { } line) {
            if (!string.IsNullOrWhiteSpace(line)) {
                lastIdx = lineIdx;
                count++;

                if (isImportRelevantLine(line)) lastRelevantIdx = lineIdx;
            }
            lineIdx++;
        }

        return new(lastIdx, lastRelevantIdx, count);
    }
}
