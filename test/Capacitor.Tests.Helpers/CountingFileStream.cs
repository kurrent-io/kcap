namespace Capacitor.Tests.Helpers;

/// <summary>A read-only transcript stream that counts the bytes read through it. A derived FileStream's async
/// reads call back into its own sync overloads, so only the outermost call counts.</summary>
/// <param name="onRead">Told of each outermost read, for a count shared across streams.</param>
public sealed class CountingFileStream(string path, Action<long>? onRead = null) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) {
    int _depth;

    public long BytesRead { get; private set; }

    public override int Read(byte[] buffer, int offset, int count) => Count(() => base.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) {
        _depth++;
        try {
            var n = base.Read(buffer);
            if (_depth == 1) Add(n);

            return n;
        } finally {
            _depth--;
        }
    }

    public override int ReadByte() {
        _depth++;
        try {
            var b = base.ReadByte();
            if (_depth == 1 && b >= 0) Add(1);

            return b;
        } finally {
            _depth--;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
        _depth++;
        try {
            var n = await base.ReadAsync(buffer, ct);
            if (_depth == 1) Add(n);

            return n;
        } finally {
            _depth--;
        }
    }

    int Count(Func<int> read) {
        _depth++;
        try {
            var n = read();
            if (_depth == 1) Add(n);

            return n;
        } finally {
            _depth--;
        }
    }

    void Add(long bytes) {
        BytesRead += bytes;
        onRead?.Invoke(bytes);
    }
}
