using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.RepoInstructions;

/// <summary>Reads a checkout's instruction files from <c>origin/HEAD</c>, never the working copy: the root's
/// <c>CLAUDE.md</c>, <c>AGENTS.md</c> and <c>GEMINI.md</c> in any case, then what they import one level deep. A symlink
/// inside the tree is followed one hop, and a file reached twice is read once.</summary>
public static class RepoInstructionsReader {
    public const int MaxFileBytes  = 64 * 1024;
    public const int MaxTotalBytes = 192 * 1024;
    public const int MaxFiles      = 12;

    const string DefaultBranch = "origin/HEAD";

    // Each import costs git calls inline in the watch loop, so a file naming thousands of them is read only this far.
    const int MaxImportReads = 4 * MaxFiles;

    // Imports looked up at all, and how many per ls-tree call, which keeps the command line short enough for Windows.
    const int MaxImportCandidates = 200;
    const int ListChunk           = 100;

    static readonly string[] RootNames = ["CLAUDE.md", "AGENTS.md", "GEMINI.md"];

    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    readonly record struct TreeEntry(string Mode, string Type, string Sha, long Size, string Path) {
        public bool IsFile    => Type == "blob" && Mode is "100644" or "100755";
        public bool IsSymlink => Type == "blob" && Mode == "120000";
    }

    /// <param name="beat">Called before each git call, for a caller whose heartbeat must not lapse while this runs.</param>
    public static async Task<RepoInstructionsRead> ReadAsync(string root, TimeProvider time, CancellationToken ct, Action? beat = null) {
        if (await GitAsync("symbolic-ref", "--quiet", "refs/remotes/origin/HEAD") is null) return RepoInstructionsRead.Not("origin/HEAD is not set");
        if (await GitAsync("ls-tree", "-z", "-l", DefaultBranch) is not { } listing) return RepoInstructionsRead.Not("origin/HEAD could not be listed");

        var blobs   = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new List<string>();
        var roots   = new List<RepoInstructionFile>();
        var imports = new List<RepoInstructionFile>();

        foreach (var entry in ParseTree(listing).Where(e => RootNames.Contains(e.Path, StringComparer.OrdinalIgnoreCase)))
            if (await ReadEntryAsync(entry, entry.Path) is { } file) roots.Add(file);

        var named  = roots.SelectMany(r => RepoInstructionImports.Of(r.Content)).Distinct(StringComparer.Ordinal).ToList();
        var wanted = named.Take(MaxImportCandidates).ToList();
        var listed = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
        foreach (var chunk in wanted.Chunk(ListChunk))
            if (await GitAsync(["--literal-pathspecs", "ls-tree", "-z", "-l", DefaultBranch, "--", ..chunk]) is { } found)
                foreach (var entry in ParseTree(found)) listed[entry.Path] = entry;

        var reads = 0;
        var notRead = 0;
        foreach (var path in wanted) {
            if (!listed.TryGetValue(path, out var entry)) continue;
            if (roots.Count + imports.Count >= MaxFiles || reads >= MaxImportReads) {
                notRead++;
                continue;
            }
            reads++;
            if (await ReadEntryAsync(entry, path) is { } file) imports.Add(file);
        }
        notRead += named.Count - wanted.Count;
        if (notRead > 0) skipped.Add($"{notRead} more import(s) left out to fit the upload limits");

        while (imports.Count > 0 && OverLimits(roots, imports)) {
            skipped.Add($"{imports[^1].Path}: left out to fit the upload limits");
            imports.RemoveAt(imports.Count - 1);
        }

        IReadOnlyList<RepoInstructionFile> files = [..roots, ..imports];
        return RepoInstructionsRead.Of(new RepoInstructionSet(files, RepoInstructionsDigest.Of(files), skipped, OverLimits(roots, imports)));

        // The file an entry names under the path it is listed by, reading through one symlink hop.
        async Task<RepoInstructionFile?> ReadEntryAsync(TreeEntry entry, string path) {
            var target = entry;
            if (entry.IsSymlink) {
                if (await GitAsync("show", $"{DefaultBranch}:{entry.Path}") is not { } link) return null;
                var parent = entry.Path.LastIndexOf('/') is var slash and > 0 ? entry.Path[..slash] + "/" : "";
                var linked = Utf8.GetString(link).TrimEnd('\n');
                if (linked.StartsWith('/') || RepoInstructionImports.Normalize(parent + linked) is not { } targetPath) return null;
                if (await GitAsync("--literal-pathspecs", "ls-tree", "-z", "-l", DefaultBranch, "--", targetPath) is not { } found || ParseTree(found) is not [var resolved]) return null;
                target = resolved;
            }
            if (!target.IsFile || !blobs.Add(target.Sha)) return null;
            if (target.Size > MaxFileBytes) {
                skipped.Add($"{path}: larger than {MaxFileBytes} bytes");
                return null;
            }
            if (await GitAsync("show", $"{DefaultBranch}:{target.Path}") is not { } bytes) return null;

            if (Array.IndexOf(bytes, (byte)0) >= 0) {
                skipped.Add($"{path}: not text");
                return null;
            }

            var content = Utf8.GetString(bytes);
            var encoded = Encoding.UTF8.GetBytes(content);
            if (encoded.Length > MaxFileBytes) {
                skipped.Add($"{path}: larger than {MaxFileBytes} bytes");
                return null;
            }

            return new RepoInstructionFile(path, Convert.ToHexStringLower(SHA256.HashData(encoded)), content);
        }

        Task<byte[]?> GitAsync(params string[] arguments) {
            beat?.Invoke();
            return GitOutput.RunAsync(root, time, ct, arguments);
        }
    }

    static bool OverLimits(List<RepoInstructionFile> roots, List<RepoInstructionFile> imports) =>
        roots.Count + imports.Count > MaxFiles
     || roots.Concat(imports).Sum(f => (long)Encoding.UTF8.GetByteCount(f.Content)) > MaxTotalBytes;

    // `<mode> SP <type> SP <sha> SP+ <size> TAB <path> NUL` per entry; a tree's size is `-`.
    static List<TreeEntry> ParseTree(byte[] listing) {
        var entries = new List<TreeEntry>();
        foreach (var record in Utf8.GetString(listing).Split('\0')) {
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;
            var fields = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 4)
                entries.Add(new TreeEntry(fields[0], fields[1], fields[2], long.TryParse(fields[3], out var size) ? size : -1, record[(tab + 1)..]));
        }
        return entries;
    }
}
