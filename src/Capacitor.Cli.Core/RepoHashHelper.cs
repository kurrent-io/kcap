using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core;

public static partial class RepoHashHelper {
    public static string ComputeRepoHash(string owner, string repoName) {
        var input = $"{owner}/{repoName}".ToLowerInvariant();
        var hash  = SHA256.HashData(Encoding.UTF8.GetBytes(input));

        return Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>Accepts the two shapes a caller may name a repo by: <c>owner/name</c>, split on the
    /// last slash so a nested-group owner (e.g. GitLab subgroups) keeps its own slashes, hashed here,
    /// or a 16-character lowercase hex hash, passed through. Uppercase hex is rejected rather than
    /// folded so a hash copied from the wrong place fails loudly. A repository with no remote is
    /// rejected by name: it is stored under an owner the server binds, which only the server can hash.</summary>
    public static bool TryParseRepoRef(string value, out string repoHash) {
        repoHash = "";

        if (string.IsNullOrWhiteSpace(value)) return false;

        if (Hash().IsMatch(value)) {
            repoHash = value;

            return true;
        }

        if (value.Any(char.IsWhiteSpace)) return false;

        var lastSlash = value.LastIndexOf('/');

        if (lastSlash <= 0 || lastSlash == value.Length - 1) return false;

        var owner = value[..lastSlash];
        var name  = value[(lastSlash + 1)..];

        if (owner.Split('/').Any(segment => segment.Length == 0) || LocalRepository.IsLocal(owner)) return false;

        repoHash = ComputeRepoHash(owner, name);

        return true;
    }

    /// <summary>Whether <paramref name="value"/> has the shape every repository hash has, 16 lowercase hex characters.</summary>
    public static bool IsRepoHash(string? value) => value is not null && Hash().IsMatch(value);

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex Hash();
}
