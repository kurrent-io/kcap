using System.Security.Cryptography;

namespace Capacitor.Cli.Core.Harness.Cursor;

/// <summary>
/// Append-only means length never decreases and rehashing the earlier byte prefix reproduces its
/// hash. Share this predicate between empirical verification and the runtime two-zone rewrite guard.
/// </summary>
public static class CursorAppendOnlyProbe {
    public readonly record struct Sample(long Length, string PrefixSha256);

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static bool PrefixStable(Sample earlier, Sample later, ReadOnlySpan<byte> laterBytes) {
        if (later.Length < earlier.Length) return false;
        if (earlier.Length > laterBytes.Length) return false;
        var reHash = Sha256Hex(laterBytes[..(int)earlier.Length]);
        return string.Equals(reHash, earlier.PrefixSha256, StringComparison.Ordinal);
    }
}
