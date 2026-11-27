using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.RepoInstructions;

public static class RepoInstructionsDigest {
    /// <summary>SHA-256 over one <c>path\0sha256\n</c> line per file, sorted by path; no files hash the empty input.</summary>
    public static string Of(IReadOnlyList<RepoInstructionFile> files) {
        var text = new StringBuilder();
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            text.Append(file.Path).Append('\0').Append(file.Sha256).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
