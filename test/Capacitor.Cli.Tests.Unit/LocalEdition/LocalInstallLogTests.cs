using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

/// <summary>What a bundle fetch and stage did stays in <c>logs/install.log</c> after the progress events are gone.</summary>
public class LocalInstallLogTests {
    const string Version = "1.3.0";

    static readonly LocalPlatform MacArm = new(true, false, false, Architecture.Arm64);

    [TempDir] public required TempDir Tmp { get; init; }

    readonly RecordingProgress<LocalInstallEvent> _events = new();

    UserHome Home => field ??= NewHome();

    LocalRoot Root => field ??= LocalRoot.Resolve(Tmp.PathTo("local"), Home);

    FakeBundle Bundle => field ??= new(Tmp.PathTo("bundle.tar.gz"), Version);

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    LocalBundleStager Stager() => new(Root, Bundle.Client(), Bundle.Key, MacArm);

    Task FetchAsync() => Stager().FetchAsync(Version, _events, CancellationToken.None);

    string Log => File.ReadAllText(LocalLogFiles.Install(Root));

    string Sha256 => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Tmp.PathTo("bundle.tar.gz"))));

    long Size => new FileInfo(Tmp.PathTo("bundle.tar.gz")).Length;

    [Test]
    public async Task A_fetch_logs_the_manifest_its_signature_the_archive_and_its_digest() {
        await FetchAsync();

        await Assert.That(Log).Contains($"Manifest {FakeBundle.BaseUrl}/osx-arm64/kcap-local-{Version}-osx-arm64.json");
        await Assert.That(Log).Contains("Manifest from the publication: signature verified");
        await Assert.That(Log).Contains($"Archive {FakeBundle.BaseUrl}/osx-arm64/kcap-local-{Version}-osx-arm64.tar.gz, {Size} bytes: downloading");
        await Assert.That(Log).Contains($"SHA-256 {Sha256} matches the manifest");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_stage_from_the_cache_logs_that_it_took_the_cached_archive() {
        await FetchAsync();

        await Stager().StageAsync(Version, "0123456789abcdef", _events, CancellationToken.None);

        var stage = Log[Log.IndexOf($"Staging {Version}", StringComparison.Ordinal)..];
        await Assert.That(stage).Contains($"kcap-local-{Version}-osx-arm64.tar.gz, {Size} bytes: taken from the cache");
        await Assert.That(stage).Contains($"SHA-256 {Sha256} matches the manifest");
        await Assert.That(stage).DoesNotContain("downloading");
    }

    /// <summary>The failure is written where it happened and stays there: the archive's digest is what refused it.</summary>
    [Test]
    public async Task A_tampered_archive_logs_the_mismatch_with_its_code() {
        Bundle.ArchiveTampered = true;

        await Assert.That(FetchAsync).Throws<LocalBundleException>();

        await Assert.That(Log).Contains("signature verified");
        await Assert.That(Log).Contains("Failed (verification_failed): The bundle's checksum does not match its manifest.");
        await Assert.That(Log).DoesNotContain("matches the manifest");
    }
}
