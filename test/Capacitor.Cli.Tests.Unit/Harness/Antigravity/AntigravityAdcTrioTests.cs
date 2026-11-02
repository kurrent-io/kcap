using Capacitor.Cli.Harness.Antigravity;

namespace Capacitor.Cli.Tests.Unit.Harness.Antigravity;

public class AntigravityAdcTrioTests {
    /// <summary>The credential path is gcloud's configuration directory plus the ADC file name, the
    /// directory being whatever <c>GcloudConfig.ConfigDir</c> resolved for the platform.</summary>
    [Test]
    public async Task The_credentials_path_is_the_adc_file_in_gcloud_config_dir() {
        using var tmp = new TempDir();
        var file = tmp.CreateFile(["gcloud", "application_default_credentials.json"], "{}");

        await Assert.That(AntigravityAdcTrio.ExistingCredentialsPath(tmp.PathTo("gcloud"))).IsEqualTo(file);
    }

    /// <summary>A path to a file that is not there would bake a half-configuration agy cannot use.</summary>
    [Test]
    public async Task No_file_or_no_config_dir_derives_nothing() {
        using var tmp = new TempDir();

        await Assert.That(AntigravityAdcTrio.ExistingCredentialsPath(tmp.PathTo("gcloud"))).IsNull();
        await Assert.That(AntigravityAdcTrio.ExistingCredentialsPath(null)).IsNull();
    }

    [Test]
    public async Task AgyTrio_reports_complete_partial_and_absent() {
        var complete = AntigravityAdcTrio.Status(new Dictionary<string, string> {
            ["GOOGLE_CLOUD_PROJECT"] = "p", ["AGY_ADC_AUTH"] = "1",
            ["GOOGLE_APPLICATION_CREDENTIALS"] = "/adc.json",
        });
        await Assert.That(complete.AnyPresent).IsTrue();
        await Assert.That(complete.Missing).IsEmpty();

        var partial = AntigravityAdcTrio.Status(new Dictionary<string, string> {
            ["GOOGLE_CLOUD_PROJECT"] = "p",
        });
        await Assert.That(partial.AnyPresent).IsTrue();
        await Assert.That(partial.Missing).IsEquivalentTo(new[] { "AGY_ADC_AUTH=1", "GOOGLE_APPLICATION_CREDENTIALS" });

        var absent = AntigravityAdcTrio.Status(new Dictionary<string, string>());
        await Assert.That(absent.AnyPresent).IsFalse();
    }

    /// <summary>agy reads only the canonical project key, and only the literal 1 selects ADC auth —
    /// either substitute leaves a unit that cannot authenticate, which is the case the complete-trio
    /// confirmation must not be printed for.</summary>
    [Test]
    public async Task AgyTrio_refuses_the_id_spelling_and_a_disabled_flag() {
        var idSpelling = AntigravityAdcTrio.Status(new Dictionary<string, string> {
            ["GOOGLE_CLOUD_PROJECT_ID"] = "p", ["AGY_ADC_AUTH"] = "1",
            ["GOOGLE_APPLICATION_CREDENTIALS"] = "/adc.json",
        });
        await Assert.That(idSpelling.Missing).IsEquivalentTo(new[] { "GOOGLE_CLOUD_PROJECT" });

        var disabled = AntigravityAdcTrio.Status(new Dictionary<string, string> {
            ["GOOGLE_CLOUD_PROJECT"] = "p", ["AGY_ADC_AUTH"] = "0",
            ["GOOGLE_APPLICATION_CREDENTIALS"] = "/adc.json",
        });
        await Assert.That(disabled.AnyPresent).IsTrue();
        await Assert.That(disabled.Missing).IsEquivalentTo(new[] { "AGY_ADC_AUTH=1" });
    }
}
