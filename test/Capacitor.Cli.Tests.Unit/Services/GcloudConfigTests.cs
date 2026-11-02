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

    static TempDir Configurations() {
        var tmp = new TempDir();
        tmp.CreateFile("active_config", "work\n");
        tmp.CreateFile(["configurations", "config_default"], "[core]\nproject = default-proj\n");
        tmp.CreateFile(["configurations", "config_work"], "[core]\nproject = work-proj\n");
        tmp.CreateFile(["configurations", "config_personal"], "[core]\nproject = personal-proj\n");
        return tmp;
    }

    [Test]
    public async Task The_active_config_file_names_the_configuration() {
        using var tmp = Configurations();

        await Assert.That(GcloudConfig.DefaultProject(tmp.Path, Env())).IsEqualTo("work-proj");
    }

    /// <summary>gcloud lets <c>CLOUDSDK_ACTIVE_CONFIG_NAME</c> override the file, so the project is the
    /// one the operator's gcloud is actually using.</summary>
    [Test]
    public async Task The_active_config_name_override_wins_over_the_file() {
        using var tmp = Configurations();

        await Assert.That(GcloudConfig.DefaultProject(tmp.Path, Env(("CLOUDSDK_ACTIVE_CONFIG_NAME", "personal"))))
            .IsEqualTo("personal-proj");
    }

    [Test]
    public async Task An_empty_override_falls_back_to_the_file() {
        using var tmp = Configurations();

        await Assert.That(GcloudConfig.DefaultProject(tmp.Path, Env(("CLOUDSDK_ACTIVE_CONFIG_NAME", ""))))
            .IsEqualTo("work-proj");
    }

    [Test]
    public async Task No_active_config_file_reads_the_default_configuration() {
        using var tmp = new TempDir();
        tmp.CreateFile(["configurations", "config_default"], "[core]\nproject = default-proj\n");

        await Assert.That(GcloudConfig.DefaultProject(tmp.Path, Env())).IsEqualTo("default-proj");
    }

    /// <summary>The name is joined into a path, so one gcloud would refuse cannot reach a file outside
    /// <c>configurations</c>.</summary>
    [Test]
    [Arguments("x/../../escape")]
    [Arguments("Work")]
    public async Task A_name_gcloud_would_refuse_derives_nothing(string name) {
        using var tmp = Configurations();
        tmp.CreateFile("escape", "[core]\nproject = escaped-proj\n");

        await Assert.That(GcloudConfig.DefaultProject(tmp.Path, Env(("CLOUDSDK_ACTIVE_CONFIG_NAME", name)))).IsNull();
    }
}
