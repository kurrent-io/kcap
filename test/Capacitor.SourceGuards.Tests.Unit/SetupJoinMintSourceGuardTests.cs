using System.Runtime.CompilerServices;

namespace Capacitor.SourceGuards.Tests.Unit;

/// <summary>
/// The join key is minted in exactly one file. <c>SetupJoinMintPlacementGuardTests</c> pins when the
/// facade mints it, but those tests only start a facade: a mint in a command handler or buried in a
/// lane is invisible to them, and mints too late (past <c>cli_first_run</c>) or only on the lane
/// that happens to run.
/// </summary>
public class SetupJoinMintSourceGuardTests {
    [Test]
    public async Task Only_CliTelemetry_mints_the_join_key() {
        var minting = FindMintingFiles(SrcRoot()).Order().ToArray();

        await Assert.That(minting).IsEquivalentTo(new[] { "CliTelemetry.cs" })
            .Because("the join key is minted once, as the facade starts; a mint anywhere else is "
                   + "either too late (past cli_first_run) or lane-dependent");
    }

    // Proves the enumeration detects a mint and ignores a commented one, against a synthetic fixture.
    [Test]
    public async Task Scanner_finds_a_real_mint_and_ignores_a_commented_one() {
        using var tmp = new TempDir();
        tmp.CreateFile("Real.cs", [
            "namespace Fixture;",
            "static class Real { static void Go() { telemetry.Join.Mint(); } }",
        ]);
        tmp.CreateFile("Documented.cs", [
            "namespace Fixture;",
            "// never Join.Mint() from here",
            "static class Documented { }",
        ]);

        await Assert.That(FindMintingFiles(tmp.Path)).IsEquivalentTo(new[] { "Real.cs" });
    }

    const string Mint = "Join.Mint()";

    static string SrcRoot() => Path.Combine(RepoRoot(), "src");

    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        if (dir is null)
            throw new InvalidOperationException($"Could not locate repo root (Capacitor.slnx) walking up from {here}");

        return dir;
    }

    // Files under root that mint in code. bin/obj are excluded — they sit inside src/ and hold
    // generated sources. A mint on a // line is documentation, not code.
    static List<string> FindMintingFiles(string root) {
        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                   .Any(segment => segment is "bin" or "obj"))) {
            var source = File.ReadAllText(file);
            var from   = 0;

            while ((from = source.IndexOf(Mint, from, StringComparison.Ordinal)) >= 0) {
                var lineStart = source.LastIndexOf('\n', from) + 1;
                if (!source[lineStart..from].Contains("//", StringComparison.Ordinal)) {
                    found.Add(Path.GetFileName(file));
                    break;
                }
                from += Mint.Length;
            }
        }

        return found;
    }
}
