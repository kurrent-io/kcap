using System.Runtime.CompilerServices;

namespace Capacitor.Cli.Commands;

/// <summary>A complete source line is acknowledged only after delivery and the source's lifecycle succeed</summary>
internal sealed class TranscriptImportCheckpoint(ImportContext ctx) : IProgress<ImportProgress> {
    bool _dropped;

    public StrongBox<int> CompleteLines { get; } = new();

    public void Report(ImportProgress value) {
        if (value is BatchDropped) _dropped = true;
        ctx.Progress?.Report(value);
    }

    public async Task RecordAsync(string sessionId, CancellationToken ct) {
        if (_dropped || ctx.TranscriptLedger is not { } ledger) return;

        await ledger.RecordAsync(ctx.HttpClient, ctx.BaseUrl, sessionId, CompleteLines.Value, ct);
    }
}
