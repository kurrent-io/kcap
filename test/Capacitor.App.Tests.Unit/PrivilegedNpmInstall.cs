using Capacitor.App.Services;
using Capacitor.App.Services.CliAdoption;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// <summary>kcap's launcher and platform package under <c>&lt;prefix&gt;/lib/node_modules/@kurrent</c>, with
/// <c>&lt;prefix&gt;/bin/kcap</c> linked into the launcher as npm links it and a second, absolute link standing for
/// a service unit's PATH entry. A sibling package in the same scope must survive.</summary>
internal sealed record PrivilegedNpmInstall(string Scope, string Sibling, IReadOnlyList<string> Packages, IReadOnlyList<LinkChange> Links) {
    public const string Version = "1.4.1-beta.2";

    public static PrivilegedNpmInstall Create(TempDir tmp, string prefix = "usr local") {
        var scope    = tmp.CreateDir(prefix, "lib", "node_modules", "@kurrent");
        var launcher = Package(scope, "kcap", "kcap.js");
        var platform = Package(scope, "kcap-darwin-arm64", "kcap");
        var sibling  = tmp.CreateFile(Path.Combine(prefix, "lib", "node_modules", "@kurrent", "other", "package.json"), "{}");
        var bundle   = tmp.CreateFile("Applications/kcap.app/Contents/MacOS/kcap");

        var npmLink = tmp.CreateDir(prefix, "bin").PathTo("kcap");
        File.CreateSymbolicLink(npmLink, "../lib/node_modules/@kurrent/kcap/bin/kcap.js");
        var unitLink = tmp.CreateDir("opt bin").PathTo("kcap");
        File.CreateSymbolicLink(unitLink, Path.Combine(launcher, "bin", "kcap.js"));

        return new PrivilegedNpmInstall(scope, sibling, [launcher, platform], [
            new LinkChange(npmLink, "../lib/node_modules/@kurrent/kcap/bin/kcap.js", bundle),
            new LinkChange(unitLink, Path.Combine(launcher, "bin", "kcap.js"), bundle),
        ]);
    }

    static string Package(string scope, string name, string binary) {
        var dir = Path.Combine(scope, name);
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "dep", "lib"));
        File.WriteAllText(Path.Combine(dir, "package.json"), $$"""{"name":"@kurrent/{{name}}","version":"{{Version}}"}""");
        File.WriteAllText(Path.Combine(dir, "bin", binary), "");
        File.WriteAllText(Path.Combine(dir, "node_modules", "dep", "lib", "index.js"), "");
        return dir;
    }

    /// <summary>Each package must still hold its own name and the recorded version.</summary>
    public PrivilegedSpec Spec(TempDir tmp, TimeSpan? goWait = null, string helper = "helper") {
        var retirement = new AsideRetirement(new ConfigRoot(tmp.CreateDir("config")), "cli-adoption-retire");
        return new PrivilegedSpec(
            Links,
            [.. Packages.Select(p => new PrivilegedEntry(
                retirement.Plan(p), PrivilegedEntryKind.Directory, "package.json", [$"\"@kurrent/{Path.GetFileName(p)}\"", $"\"{Version}\""]))],
            tmp.PathTo(helper, "control"), tmp.PathTo(helper, "status"), goWait ?? TimeSpan.FromSeconds(30));
    }

    public async Task AssertUntouchedAsync(PrivilegedSpec spec) {
        foreach (var link in Links) await Assert.That(new FileInfo(link.Path).LinkTarget).IsEqualTo(link.Original);
        foreach (var (entry, _, _, _) in spec.Entries) {
            await Assert.That(File.Exists(Path.Combine(entry.Original, "package.json"))).IsTrue();
            await Assert.That(Path.Exists(entry.Aside)).IsFalse();
        }
    }
}
