using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.Harness.Dsh;

/// <summary>
/// Maps a dsh session id onto the server's session-id contract. dsh names sessions
/// <c>session-&lt;guid&gt;</c> (44+ characters), and the read model drops any id longer than 36, so
/// the embedded GUID becomes the id in its dashless form. The live hook and import must agree, or
/// one session splits into two streams.
/// </summary>
public static class DshSessionId {
    public static string Canonicalize(string rawId) {
        if (string.IsNullOrEmpty(rawId)) return rawId;

        if (Guid.TryParse(rawId, out var whole)) return whole.ToString("N");

        if (rawId.Length >= 36 && Guid.TryParse(rawId[^36..], out var tail)) return tail.ToString("N");

        if (rawId.Length <= 36) return rawId;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawId));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
