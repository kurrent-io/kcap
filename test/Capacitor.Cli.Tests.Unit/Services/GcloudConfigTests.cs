using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Where gcloud keeps its configuration, and so the ADC file the CLI hands a spawned daemon. The
/// platform is a parameter, so both arms run on any host.
/// </summary>
public class GcloudConfigTests {
    static readonly UserHome PosixHome = new("/home/op");

    static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        key => vars.FirstOrDefault(v => v.Key == key).Value;

    [Test]
    public async Task Posix_reads_the_config_dir_under_home() =>
        await Assert.That(GcloudConfig.ConfigDir(PosixHome, isWindows: false, Env()))
            .IsEqualTo(Path.Combine("/home/op", ".config", "gcloud"));

    /// <summary>gcloud on Windows writes under the roaming profile, never under the home directory.</summary>
    [Test]
    public async Task Windows_reads_the_config_dir_under_appdata() =>
        await Assert.That(GcloudConfig.ConfigDir(
                new UserHome(@"C:\Users\op"), isWindows: true, Env(("APPDATA", @"C:\Users\op\AppData\Roaming"))))
            .IsEqualTo(Path.Combine(@"C:\Users\op\AppData\Roaming", "gcloud"));

    [Test]
    public async Task Windows_without_appdata_derives_nothing() =>
        await Assert.That(GcloudConfig.ConfigDir(new UserHome(@"C:\Users\op"), isWindows: true, Env())).IsNull();

    /// <summary>gcloud honours <c>CLOUDSDK_CONFIG</c> ahead of its default on every platform.</summary>
    [Test]
    [Arguments(false, "/srv/gcloud")]
    [Arguments(true, @"D:\gcloud")]
    public async Task An_absolute_cloudsdk_config_wins(bool isWindows, string configured) =>
        await Assert.That(GcloudConfig.ConfigDir(
                PosixHome, isWindows, Env(("CLOUDSDK_CONFIG", configured), ("APPDATA", @"C:\Users\op\AppData\Roaming"))))
            .IsEqualTo(configured);

    /// <summary>A relative override is relative to wherever gcloud ran, which the daemon cannot know,
    /// so nothing is derived rather than a default that is not where the credential went.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_relative_cloudsdk_config_derives_nothing(bool isWindows) =>
        await Assert.That(GcloudConfig.ConfigDir(
                PosixHome, isWindows, Env(("CLOUDSDK_CONFIG", "gcloud-conf"), ("APPDATA", @"C:\Users\op\AppData\Roaming"))))
            .IsNull();

    [Test]
    public async Task No_config_dir_means_no_project() =>
        await Assert.That(GcloudConfig.DefaultProject(null)).IsNull();
}
