namespace Capacitor.Cli.Recording;

/// <summary>Read-only view of <c>inner</c> that reports EOF after <c>limit</c> bytes, so a
/// StreamReader over it can't read past a length sampled before a concurrent append.</summary>
internal sealed class LengthLimitedReadStream(Stream inner, long limit, Capacitor.Cli.Core.TranscriptLineIndex? observer = null) : Stream {
    long _consumed;

    public override int Read(byte[] buffer, int offset, int count) {
        var allowed = (int)Math.Min(count, limit - _consumed);
        if (allowed <= 0) return 0;
        var n = inner.Read(buffer, offset, allowed);
        _consumed += n;
        observer?.Observe(buffer.AsSpan(offset, n));
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) {
        var allowed = (int)Math.Min(buffer.Length, limit - _consumed);
        if (allowed <= 0) return 0;
        var n = await inner.ReadAsync(buffer[..allowed], ct);
        _consumed += n;
        observer?.Observe(buffer.Span[..n]);
        return n;
    }

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => limit;

    public override long Position {
        get => _consumed;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
