using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.RepoInstructions;

/// <summary>The digest each server last accepted for a repository, so an unchanged set is not uploaded again. Every
/// operation fails open: an unreadable cache reads as empty and a failed write is dropped.</summary>
public sealed class RepoInstructionsDigestCache(string root) {
    const int MaxBytes = 256;

    public static RepoInstructionsDigestCache Default(ConfigRoot config) => new(config.Path("cache", "repo-instructions-v1"));

    public string? Read(string serverUrl, string repoHash) {
        try {
            var path = PathFor(serverUrl, repoHash);
            var info = new FileInfo(path);
            return info is { Exists: true, Length: <= MaxBytes } ? File.ReadAllText(path).Trim() : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    public void Write(string serverUrl, string repoHash, string digest) {
        string? temp = null;
        try {
            Directory.CreateDirectory(root);
            temp = Path.Combine(root, $"{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                if (!OperatingSystem.IsWindows())
                    try { File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* ignored */ }
                stream.Write(Encoding.UTF8.GetBytes(digest));
            }
            File.Move(temp, PathFor(serverUrl, repoHash), overwrite: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The next session uploads again; the server answers an unchanged digest without storing it.
        } finally {
            try { if (temp is not null && File.Exists(temp)) File.Delete(temp); } catch { /* ignored */ }
        }
    }

    // Scheme and host fold case; a base path is kept as given, since two paths differing in case can be two servers.
    internal static string ServerKey(string serverUrl) =>
        Uri.TryCreate(serverUrl.TrimEnd('/'), UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority.ToLowerInvariant()}{uri.AbsolutePath.TrimEnd('/')}"
            : serverUrl.TrimEnd('/');

    string PathFor(string serverUrl, string repoHash) {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{ServerKey(serverUrl)}\n{repoHash}")))[..32];
        return Path.Combine(root, key);
    }
}
