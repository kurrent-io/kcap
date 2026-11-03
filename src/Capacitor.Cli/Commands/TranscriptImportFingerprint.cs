using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Commands;

internal sealed class TranscriptImportFingerprint(int expectedLines = 0, string? expectedSha256 = null) : IDisposable {
    readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    string? _pending;
    int _read;
    int _committed;

    public string? Sha256 { get; private set; }

    public void Add(string line) {
        CommitPending();
        _pending = line;
        _read++;
    }

    public void Complete(int completeLines) {
        if (completeLines == _read) CommitPending();
        else if (completeLines != _committed) throw new SessionImporter.TranscriptDeliveryAbortedException();
        if (_committed < expectedLines) throw new SessionImporter.TranscriptDeliveryAbortedException();
        Sha256 = Convert.ToHexStringLower(_hash.GetCurrentHash());
    }

    void CommitPending() {
        if (_pending is not { } line) return;
        _hash.AppendData(Encoding.UTF8.GetBytes(line));
        _hash.AppendData("\n"u8);
        _pending = null;
        _committed++;
        if (_committed == expectedLines && expectedSha256 != Convert.ToHexStringLower(_hash.GetCurrentHash()))
            throw new SessionImporter.TranscriptDeliveryAbortedException();
    }

    public static async Task<string?> ReadPrefixAsync(string path, int lines, CancellationToken ct) {
        if (lines < 1) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        using var fingerprint = new TranscriptImportFingerprint();
        for (var i = 0; i < lines; i++) {
            if (await reader.ReadLineAsync(ct) is not { } line) return null;
            fingerprint.Add(line);
        }
        if (await reader.ReadLineAsync(ct) is null) {
            if (stream.Length == 0) return null;
            stream.Position = stream.Length - 1;
            if (stream.ReadByte() is not ('\n' or '\r')) return null;
        }
        fingerprint.Complete(lines);
        return fingerprint.Sha256;
    }

    public void Dispose() => _hash.Dispose();
}
