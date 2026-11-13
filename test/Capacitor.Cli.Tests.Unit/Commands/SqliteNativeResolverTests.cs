using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Covers the on-demand native-SQLite loader used by `kcap import --opencode`.
/// The AOT binary doesn't bundle e_sqlite3; <see cref="SqliteNativeResolver"/> downloads
/// the pristine per-RID native, integrity-checks it, caches it, and loads it.
/// </summary>
public class SqliteNativeResolverTests {
    /// <summary>
    /// Drift guard for ALL shipped RIDs, not just the test runner's: the build output carries
    /// every runtime native of the SQLite package, so a single CI agent can validate all six
    /// pins. Catches a typo in (say) the macOS or musl-arm64 hash that linux/windows CI would
    /// otherwise miss, letting the release publish an asset users can't verify.
    /// Fails until EngineVersion + the Assets hashes are regenerated after a bundle bump.
    /// </summary>
    [Test]
    public async Task pinned_hashes_match_restored_natives_for_all_rids() {
        await Assert.That(RestoredSqliteVersion()).IsEqualTo(SqliteNativeResolver.EngineVersion)
            .Because("EngineVersion must name the SQLite package the bundle restores");
        foreach (var (rid, asset) in SqliteNativeResolver.Assets) {
            var pristine = PristineNativePath(rid);
            await Assert.That(File.Exists(pristine))
                .IsTrue().Because($"restored SQLite native expected at {pristine} — " +
                                  "if the bundle version changed, update EngineVersion + Assets pins");
            await Assert.That(Sha256(pristine)).IsEqualTo(asset.Sha256)
                .Because($"pin for {rid} must match the restored native bytes");
        }
    }

    /// <summary>The resolver's asset name must match release.yml's `<c>&lt;base&gt;-&lt;rid&gt;.&lt;ext&gt;</c>`.</summary>
    [Test]
    public async Task asset_names_follow_release_naming_convention() {
        foreach (var (rid, a) in SqliteNativeResolver.Assets) {
            var expected = $"{Path.GetFileNameWithoutExtension(a.FileName)}-{rid}{Path.GetExtension(a.FileName)}";
            await Assert.That(a.AssetName).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task downloads_from_mirror_verifies_caches_and_loads() {
        var rid = SqliteNativeResolver.CurrentRid();
        using var mirror = new TempDir();
        using var cache  = new TempDir();
        var asset = SqliteNativeResolver.Assets[rid];

        // Seed a local mirror (the airgap path) with the pristine native.
        File.Copy(PristineNativePath(rid), mirror.PathTo(asset.AssetName));

        var path = SqliteNativeResolver.EnsureNativeLibrary(rid, mirror.Path, cache.Path, "0.0.0");

        await Assert.That(File.Exists(path)).IsTrue();
        await Assert.That(Sha256(path)).IsEqualTo(asset.Sha256);
        await Assert.That(IsLoadableSqlite(path)).IsTrue()
            .Because("the cached native must be a real, loadable SQLite engine");

        // Second call is cache-only: a mirror path that does not exist cannot be the source.
        var again = SqliteNativeResolver.EnsureNativeLibrary(rid, mirror.PathTo("absent"), cache.Path, "0.0.0");
        await Assert.That(again).IsEqualTo(path);
    }

    [Test]
    public async Task rejects_a_corrupt_download_and_caches_nothing() {
        var rid = SqliteNativeResolver.CurrentRid();
        using var mirror = new TempDir();
        using var cache  = new TempDir();
        var asset = SqliteNativeResolver.Assets[rid];

        File.WriteAllBytes(mirror.PathTo(asset.AssetName), [0xDE, 0xAD, 0xBE, 0xEF]);

        await Assert.That(() =>
                SqliteNativeResolver.EnsureNativeLibrary(rid, mirror.Path, cache.Path, "0.0.0"))
            .Throws<DllNotFoundException>();

        var wouldBe = cache.PathTo(SqliteNativeResolver.EngineVersion, rid, asset.FileName);
        await Assert.That(File.Exists(wouldBe)).IsFalse()
            .Because("a failed integrity check must not leave a half-written lib in the cache");
    }

    [Test]
    public async Task unsupported_rid_throws_actionable_error() {
        using var cache = new TempDir();
        await Assert.That(() =>
                SqliteNativeResolver.EnsureNativeLibrary("plan9-pdp11", null, cache.Path, "0.0.0"))
            .Throws<DllNotFoundException>();
    }

    // --- helpers -----------------------------------------------------------

    // A RID-less build copies every RID's native from the SQLite package (which
    // SQLitePCLRaw.bundle_e_sqlite3 restores, NOT SQLitePCLRaw.lib.e_sqlite3) under runtimes/,
    // so the pins are checked against build output and never against the NuGet cache.
    static string PristineNativePath(string rid) =>
        Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native",
            SqliteNativeResolver.Assets[rid].FileName);

    static string? RestoredSqliteVersion() {
        var depsFile = Path.Combine(AppContext.BaseDirectory,
            $"{typeof(SqliteNativeResolverTests).Assembly.GetName().Name}.deps.json");
        using var deps = JsonDocument.Parse(File.ReadAllText(depsFile));
        return deps.RootElement.GetProperty("libraries").EnumerateObject()
            .Select(lib => lib.Name.Split('/'))
            .Where(parts => parts is ["SQLite", _])
            .Select(parts => parts[1])
            .SingleOrDefault();
    }

    static string Sha256(string path) {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    delegate IntPtr LibVersionFn();

    static bool IsLoadableSqlite(string path) {
        if (!NativeLibrary.TryLoad(path, out var h)) return false;
        try {
            if (!NativeLibrary.TryGetExport(h, "sqlite3_libversion", out var fn)) return false;
            var version = Marshal.PtrToStringAnsi(Marshal.GetDelegateForFunctionPointer<LibVersionFn>(fn)());
            return version is not null && version.StartsWith('3');
        } finally {
            NativeLibrary.Free(h);
        }
    }
}
