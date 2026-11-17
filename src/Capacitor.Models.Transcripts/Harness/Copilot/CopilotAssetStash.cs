namespace Capacitor.Models.Transcripts.Harness.Copilot;

/// Decoded <c>session.binary_asset</c> bytes awaiting the <c>user.message</c> that names them, keyed by Copilot's
/// content-addressed asset id and bounded drop-oldest. A lookup leaves the asset in place: a line the caller fails to
/// store is projected again on retry, and must pair again.
internal sealed class CopilotAssetStash {
    public const int  MaxEntries = 8;
    public const long MaxBytes   = 32L * 1024 * 1024;

    readonly LinkedList<string>                                     _order = new();
    readonly Dictionary<string, (string MimeType, byte[] Data)> _byId  = new(StringComparer.Ordinal);
    long                                                            _bytes;

    public int Count => _byId.Count;

    /// A repeated id keeps the first bytes, since the id addresses the content. An asset past the caps on its own
    /// is evicted at once, and its prompt lands without it.
    public void Put(string assetId, string mimeType, byte[] data) {
        if (!_byId.TryAdd(assetId, (mimeType, data))) return;

        _order.AddLast(assetId);
        _bytes += data.Length;

        while (_order.Count > 0 && (_byId.Count > MaxEntries || _bytes > MaxBytes)) {
            var oldest = _order.First!.Value;
            _order.RemoveFirst();
            if (_byId.Remove(oldest, out var evicted)) _bytes -= evicted.Data.Length;
        }
    }

    public (string MimeType, byte[] Data)? Get(string assetId) => _byId.TryGetValue(assetId, out var asset) ? asset : null;
}
