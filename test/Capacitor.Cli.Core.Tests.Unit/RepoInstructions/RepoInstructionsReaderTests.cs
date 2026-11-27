using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.RepoInstructions;

namespace Capacitor.Cli.Core.Tests.Unit.RepoInstructions;

/// <summary>What the reader takes from a real clone's <c>origin/HEAD</c>.</summary>
[ParallelLimiter<SubprocessLimit>]
public class RepoInstructionsReaderTests {
    [TempDir] public required TempDir Tmp { get; init; }

    // A clone of an upstream whose one commit <paramref name="seed"/> stages; the clone's origin/HEAD names it.
    GitRepo CloneOf(Action<GitRepo> seed) {
        var upstream = GitRepo.InitIn(Tmp.CreateDir("upstream"));
        seed(upstream);
        upstream.Commit("seed");

        return upstream.Clone(Tmp.PathTo("clone"));
    }

    static void Write(GitRepo repo, string path, string content) {
        repo.CreateFile(path, content);
        repo.Add(path);
    }

    // Staged straight into the index, so no platform needs symlink support.
    static void Link(GitRepo repo, string path, string target) {
        var blob = repo.DoWithInput(Encoding.UTF8.GetBytes(target), "hash-object", "-w", "--stdin").Text;
        repo.Do("update-index", "--add", "--cacheinfo", $"120000,{blob},{path}");
    }

    // Staged without a working file, so names differing only in case coexist on any filesystem.
    static void Blob(GitRepo repo, string path, string content) {
        var blob = repo.DoWithInput(Encoding.UTF8.GetBytes(content), "hash-object", "-w", "--stdin").Text;
        repo.Do("update-index", "--add", "--cacheinfo", $"100644,{blob},{path}");
    }

    static async Task<RepoInstructionSet> ReadAsync(string root) =>
        (await RepoInstructionsReader.ReadAsync(root, TimeProvider.System, CancellationToken.None)).Set!;

    static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    [Test]
    public async Task Root_files_come_from_the_default_branch_in_any_case_under_their_own_spelling() {
        var clone = CloneOf(up => {
            Write(up, "Claude.md", "claude rules\n");
            Write(up, "agents.MD", "agents rules\n");
            Write(up, "README.md", "not an instruction file\n");
        });
        clone.CreateFile("Claude.md", "a local edit");
        clone.CommitAll("local only");

        var set = await ReadAsync(clone);

        await Assert.That(set.Files).IsEquivalentTo(new[] {
            new RepoInstructionFile("Claude.md", Sha("claude rules\n"), "claude rules\n"),
            new RepoInstructionFile("agents.MD", Sha("agents rules\n"), "agents rules\n"),
        });
    }

    [Test]
    public async Task A_checkout_without_origin_HEAD_reads_nothing() {
        using var repo = GitRepo.CreateWithCommit();
        repo.CreateFile("CLAUDE.md", "rules");
        repo.CommitAll("rules");

        var read = await RepoInstructionsReader.ReadAsync(repo, TimeProvider.System, CancellationToken.None);

        await Assert.That(read.Set).IsNull();
        await Assert.That(read.Unavailable).IsNotNull();
    }

    /// <summary>A file the listing names but git cannot read fails the read, so the set is never sent without it.</summary>
    [Test]
    public async Task A_listed_file_git_cannot_read_fails_the_whole_read() {
        var clone = CloneOf(up => {
            Write(up, "AGENTS.md", "agents\n");
            Write(up, "CLAUDE.md", "claude\n");
        });
        var blob = clone.RevParse("origin/HEAD:CLAUDE.md");
        var loose = Path.Combine(clone.Path, ".git", "objects", blob[..2], blob[2..]);
        File.SetAttributes(loose, FileAttributes.Normal);
        File.Delete(loose);

        var read = await RepoInstructionsReader.ReadAsync(clone, TimeProvider.System, CancellationToken.None);

        await Assert.That(read.Set).IsNull();
        await Assert.That(read.Unavailable).Contains("CLAUDE.md");
    }

    [Test]
    public async Task A_symlink_is_followed_one_hop_inside_the_tree_and_a_blob_is_read_once() {
        var clone = CloneOf(up => {
            Write(up, "AGENTS.md", "shared\n");
            Write(up, "docs/gemini.md", "gemini\n");
            Write(up, "docs/hop.md", "two hops away\n");
            Link(up, "CLAUDE.md", "AGENTS.md");
            Link(up, "GEMINI.md", "docs/gemini.md");
        });

        var set = await ReadAsync(clone);

        await Assert.That(set.Files.Select(f => (f.Path, f.Content))).IsEquivalentTo(new[] { ("AGENTS.md", "shared\n"), ("GEMINI.md", "gemini\n") });
    }

    [Test]
    public async Task A_symlink_leaving_the_tree_or_naming_another_symlink_is_dropped() {
        var clone = CloneOf(up => {
            Write(up, "docs/real.md", "real\n");
            Link(up, "docs/second.md", "real.md");
            Link(up, "CLAUDE.md", "../outside.md");
            Link(up, "AGENTS.md", "docs/second.md");
        });

        await Assert.That((await ReadAsync(clone)).Files).IsEmpty();
    }

    [Test]
    public async Task Imports_are_followed_one_level_and_only_inside_the_repo() {
        var clone = CloneOf(up => {
            Write(up, "CLAUDE.md", "See @docs/a.md\n@~/home.md @/etc/hosts @../out.md @docs/missing.md\n"
                                 + "mail me@example.com\n```\n@docs/fenced.md\n```\n");
            Write(up, "docs/a.md", "@docs/b.md\n");
            Write(up, "docs/b.md", "nested\n");
            Write(up, "docs/fenced.md", "fenced\n");
        });

        var set = await ReadAsync(clone);

        await Assert.That(set.Files.Select(f => f.Path)).IsEquivalentTo(new[] { "CLAUDE.md", "docs/a.md" });
    }

    [Test]
    public async Task A_file_over_the_size_limit_is_skipped_and_said_so() {
        var clone = CloneOf(up => {
            Write(up, "AGENTS.md", new string('x', RepoInstructionsReader.MaxFileBytes + 1));
            Write(up, "CLAUDE.md", "small\n");
        });

        var set = await ReadAsync(clone);

        await Assert.That(set.Files.Select(f => f.Path)).IsEquivalentTo(new[] { "CLAUDE.md" });
        await Assert.That(set.Skipped).HasSingleItem();
        await Assert.That(set.Skipped[0]).StartsWith("AGENTS.md");
    }

    [Test]
    public async Task A_file_holding_a_NUL_is_skipped_as_not_text_and_the_rest_kept() {
        var clone = CloneOf(up => {
            Blob(up, "AGENTS.md", "binary\0data");
            Write(up, "CLAUDE.md", "text\n");
        });

        var set = await ReadAsync(clone);

        await Assert.That(set.Files.Select(f => f.Path)).IsEquivalentTo(new[] { "CLAUDE.md" });
        await Assert.That(set.Skipped).IsEquivalentTo(new[] { "AGENTS.md: not text" });
    }

    [Test]
    public async Task Imports_past_the_file_count_are_not_read() {
        var names = Enumerable.Range(1, RepoInstructionsReader.MaxFiles + 2).Select(i => $"docs/{i:00}.md").ToList();
        var clone = CloneOf(up => {
            Write(up, "CLAUDE.md", string.Join('\n', names.Select(n => "@" + n)));
            foreach (var name in names) Write(up, name, name);
        });

        var set = await ReadAsync(clone);

        await Assert.That(set.Files.Select(f => f.Path)).IsEquivalentTo(["CLAUDE.md", ..names[..(RepoInstructionsReader.MaxFiles - 1)]]);
        await Assert.That(set.Skipped).IsEquivalentTo(new[] { "3 more import(s) left out to fit the upload limits" });
    }

    [Test]
    public async Task Root_files_over_the_total_in_several_spellings_are_over_the_limits() {
        var clone = CloneOf(up => {
            foreach (var (name, fill) in new[] { ("CLAUDE.md", 'a'), ("claude.md", 'b'), ("Claude.md", 'c'), ("AGENTS.md", 'd') })
                Blob(up, name, new string(fill, RepoInstructionsReader.MaxFileBytes));
        });

        await Assert.That((await ReadAsync(clone)).OverLimits).IsTrue();
    }

    /// <summary>A closing fence carries nothing but whitespace after its marker, so a fence line with a label stays inside.</summary>
    [Test]
    public async Task A_fence_line_with_a_label_does_not_close_the_fence() {
        var imports = RepoInstructionImports.Of("```text\n```example\n@docs/inside.md\n``` \t\n@docs/outside.md\n");

        await Assert.That(imports).IsEquivalentTo(new[] { "docs/outside.md" });
    }

    [Test]
    public async Task The_digest_hashes_sorted_path_and_sha_lines_and_no_files_hash_the_empty_input() {
        var b = new RepoInstructionFile("b.md", "bb", "");
        var a = new RepoInstructionFile("a.md", "aa", "");

        await Assert.That(RepoInstructionsDigest.Of([b, a])).IsEqualTo(Sha("a.md\0aa\nb.md\0bb\n"));
        await Assert.That(RepoInstructionsDigest.Of([])).IsEqualTo(Sha(""));
    }
}
