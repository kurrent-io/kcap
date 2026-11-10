using System.Xml.Linq;
using Capacitor.Cli.Services;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Tests.Unit.Services;

public class LaunchdUnitTests {
    static ServiceSpec Spec(string id = "laptop") => new(
        ServiceId: id,
        DaemonBinaryPath: "/opt/kcap/kcap-daemon",
        LogPath: "/home/u/.config/kcap/daemon-laptop.log",
        Environment: new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin", ["KCAP_PROFILE"] = "work" },
        ExtraArgs: ["--max-agents", "8"]);

    [Test]
    public async Task Label_is_reverse_dns_with_id() {
        await Assert.That(LaunchdUnit.Label("laptop")).IsEqualTo("io.kurrent.kcap.daemon.laptop");
    }

    [Test]
    public async Task Plist_is_well_formed_xml_and_carries_args_and_env() {
        var plist = LaunchdUnit.Plist(Spec());
        var doc   = XDocument.Parse(plist); // throws if malformed
        await Assert.That(doc).IsNotNull();
        await Assert.That(plist).Contains("<string>/opt/kcap/kcap-daemon</string>");
        await Assert.That(plist).Contains("<string>--name</string>");
        await Assert.That(plist).Contains("<string>laptop</string>");
        await Assert.That(plist).Contains("<string>--max-agents</string>");
        await Assert.That(plist).Contains("<key>PATH</key>");
        await Assert.That(plist).Contains("<key>KCAP_PROFILE</key>");
        await Assert.That(plist).Contains("<key>SuccessfulExit</key>");
    }

    /// <summary>An Adaptive job runs at background priority, where the heartbeat starves under load.</summary>
    [Test]
    public async Task Plist_runs_the_daemon_as_a_standard_process() {
        var dict = XDocument.Parse(LaunchdUnit.Plist(Spec())).Root!.Element("dict")!;
        var key  = dict.Elements("key").Single(k => k.Value == "ProcessType");
        await Assert.That(key.ElementsAfterSelf().First().Value).IsEqualTo("Standard");
    }

    [Test]
    public async Task Plist_escapes_metacharacters_in_values() {
        var spec  = Spec() with { DaemonBinaryPath = "/opt/a&b/kcap-daemon" };
        var plist = LaunchdUnit.Plist(spec);
        XDocument.Parse(plist); // must still parse
        await Assert.That(plist).Contains("/opt/a&amp;b/kcap-daemon");
    }

    [Test]
    public async Task IdFromPlistFileName_extracts_the_id() {
        await Assert.That(LaunchdUnit.IdFromPlistFileName("io.kurrent.kcap.daemon.laptop.plist"))
            .IsEqualTo("laptop");
        await Assert.That(LaunchdUnit.IdFromPlistFileName("unrelated.plist")).IsNull();
    }

    [Test]
    public async Task BinaryFromPlist_returns_program_argument_zero_not_the_label() {
        var plist = LaunchdUnit.Plist(Spec());
        // Regression: the Label is the first <string> in the document; the binary
        // is the first <string> inside <array> (ProgramArguments).
        await Assert.That(LaunchdPrint.Binary(plist)).IsEqualTo("/opt/kcap/kcap-daemon");
    }

    [Test]
    public async Task EnvFromPlist_round_trips_what_Plist_writes() {
        var spec = Spec() with {
            Environment = new Dictionary<string, string> {
                ["KCAP_PROFILE"]              = "acme",
                ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
                ["KCAP_EXPECT_SERVER_URL"]    = "https://s",
            },
        };
        var xml = LaunchdUnit.Plist(spec);
        var env = LaunchdPrint.Environment(xml);

        await Assert.That(env["KCAP_PROFILE"]).IsEqualTo("acme");
        await Assert.That(env["KCAP_CONSENT_SEED_DEFAULT"]).IsEqualTo("prompt");
        await Assert.That(env["KCAP_EXPECT_SERVER_URL"]).IsEqualTo("https://s");
    }

    [Test]
    public async Task EnvFromPlist_on_plist_without_env_dict_returns_empty() {
        var xml = LaunchdUnit.Plist(Spec() with { Environment = new Dictionary<string, string>() });
        await Assert.That(LaunchdPrint.Environment(xml)).IsEmpty();
    }

    /// <summary>A genuinely never-created parent chain must still classify as Absent — link/file
    /// evidence detection must not turn every missing directory level into Unreadable.</summary>
    [Test]
    public async Task TryReadPlist_missing_parent_directories_is_still_absent() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("never-created", "sub", "x.plist");

        var status = LaunchdUnit.TryReadPlist(path, out var content);

        await Assert.That(status).IsEqualTo(LaunchdUnit.PlistRead.Absent);
        await Assert.That(content).IsNull();
    }

    [Test]
    public async Task StatusFromPrint_maps_exit_and_state() {
        await Assert.That(LaunchdUnit.StatusFromPrint(exitCode: 1, stdout: "")).IsEqualTo(ServiceState.NotInstalled);
        await Assert.That(LaunchdUnit.StatusFromPrint(0, "state = running")).IsEqualTo(ServiceState.Running);
        await Assert.That(LaunchdUnit.StatusFromPrint(0, "state = not running")).IsEqualTo(ServiceState.Installed);
    }

    [Test]
    [Arguments("\tspawn type = daemon (3)\n", "daemon")]
    [Arguments("\tspawn type = interactive (4)\n", "interactive")]
    [Arguments("\tspawn type = background (5)\n", "background")]
    [Arguments("\tspawn type = adaptive (6)\n", "adaptive")]
    [Arguments("\tspawn type = app (1)\n", "app")]
    [Arguments("\tspawn type = Adaptive (6)\r\n", "adaptive")]
    public async Task LoadedSpawnType_reads_the_word(string print, string expected) {
        await Assert.That(LaunchdUnit.LoadedSpawnType($"gui/501/x = {{\n{print}}}\n")).IsEqualTo(expected);
    }

    [Test]
    public async Task LoadedSpawnType_is_null_without_the_line() {
        await Assert.That(LaunchdUnit.LoadedSpawnType("gui/501/x = {\n\tstate = running\n}\n")).IsNull();
        await Assert.That(LaunchdUnit.LoadedSpawnType("gui/501/x = {\n\tspawn type = \n}\n")).IsNull();
    }

    const string CanonicalAdaptive =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        "<plist version=\"1.0\">\n<dict>\n" +
        "\t<key>Label</key>\n\t<string>io.kurrent.kcap.daemon.laptop</string>\n" +
        "\t<key>ProcessType</key>\n\t<string>Adaptive</string>\n" +
        "\t<key>ProgramArguments</key>\n\t<array>\n\t\t<string>/opt/kcap/kcap-daemon</string>\n\t</array>\n" +
        "</dict>\n</plist>\n";

    [Test]
    public async Task UpgradeProcessType_rewrites_the_canonical_layout_and_keeps_every_other_byte() {
        var upgraded = LaunchdUnit.UpgradeProcessType(CanonicalAdaptive);
        await Assert.That(upgraded).IsEqualTo(CanonicalAdaptive.Replace("<string>Adaptive</string>", "<string>Standard</string>"));
    }

    [Test]
    public async Task UpgradeProcessType_keeps_crlf_and_a_missing_final_newline() {
        var crlf = CanonicalAdaptive.Replace("\n", "\r\n").TrimEnd('\r', '\n');
        var upgraded = LaunchdUnit.UpgradeProcessType(crlf);
        await Assert.That(upgraded).IsEqualTo(crlf.Replace("<string>Adaptive</string>", "<string>Standard</string>"));
    }

    [Test]
    public async Task UpgradeProcessType_rewrites_the_writer_layout_and_a_background_value() {
        var writer = LaunchdUnit.Plist(Spec()).Replace("<string>Standard</string>", "<string>Adaptive</string>");
        await Assert.That(LaunchdUnit.UpgradeProcessType(writer)).IsEqualTo(LaunchdUnit.Plist(Spec()));
        var background = CanonicalAdaptive.Replace("Adaptive", "Background");
        await Assert.That(LaunchdUnit.UpgradeProcessType(background)).IsEqualTo(background.Replace("<string>Background</string>", "<string>Standard</string>"));
    }

    [Test]
    public async Task UpgradeProcessType_skips_a_comment_between_key_and_value_and_a_decoy_comment() {
        var decoy = CanonicalAdaptive
            .Replace("\t<key>ProcessType</key>\n\t<string>Adaptive</string>\n",
                "\t<!-- <key>ProcessType</key><string>Adaptive</string> -->\n\t<key>ProcessType</key>\n\t<!-- real value below -->\n\t<string>Adaptive</string>\n");
        var upgraded = LaunchdUnit.UpgradeProcessType(decoy)!;
        await Assert.That(upgraded).Contains("<!-- <key>ProcessType</key><string>Adaptive</string> -->");
        await Assert.That(upgraded).Contains("<!-- real value below -->\n\t<string>Standard</string>");
    }

    [Test]
    [Arguments("<string>Standard</string>")]
    [Arguments("<string>Interactive</string>")]
    [Arguments("<string><![CDATA[Adaptive]]></string>")]
    [Arguments("<string>Adap<!-- x -->tive</string>")]
    public async Task UpgradeProcessType_returns_null_for_values_it_must_not_touch(string value) {
        await Assert.That(LaunchdUnit.UpgradeProcessType(CanonicalAdaptive.Replace("<string>Adaptive</string>", value))).IsNull();
    }

    [Test]
    public async Task UpgradeProcessType_returns_null_for_missing_duplicate_or_nested_keys_and_malformed_xml() {
        await Assert.That(LaunchdUnit.UpgradeProcessType(CanonicalAdaptive.Replace("\t<key>ProcessType</key>\n\t<string>Adaptive</string>\n", ""))).IsNull();
        await Assert.That(LaunchdUnit.UpgradeProcessType(CanonicalAdaptive.Replace("</dict>", "\t<key>ProcessType</key>\n\t<string>Adaptive</string>\n</dict>"))).IsNull();
        var nestedOnly = CanonicalAdaptive.Replace("\t<key>ProcessType</key>\n\t<string>Adaptive</string>\n",
            "\t<key>Inner</key>\n\t<dict>\n\t\t<key>ProcessType</key>\n\t\t<string>Adaptive</string>\n\t</dict>\n");
        await Assert.That(LaunchdUnit.UpgradeProcessType(nestedOnly)).IsNull();
        await Assert.That(LaunchdUnit.UpgradeProcessType("<plist><dict><key>ProcessType</key>")).IsNull();
    }

    static string EnvPlist(Dictionary<string, string> env) =>
        LaunchdUnit.Plist(new ServiceSpec("test", "/opt/kcap/kcap-daemon", "/tmp/daemon-test.log", env, []));

    [Test]
    public async Task WithEnvironmentValue_replaces_one_variable_and_keeps_the_others() {
        var plist = EnvPlist(new() { ["PATH"] = "/usr/bin", ["KCAP_PROFILE"] = "work" });

        var rewritten = LaunchdUnit.WithEnvironmentValue(plist, "PATH", "/usr/bin:/a&b");

        await Assert.That(rewritten).IsNotNull();
        var env = LaunchdPrint.Environment(rewritten!);
        await Assert.That(env["PATH"]).IsEqualTo("/usr/bin:/a&b");
        await Assert.That(env["KCAP_PROFILE"]).IsEqualTo("work");
        await Assert.That(env.Count).IsEqualTo(2);
    }

    [Test]
    public async Task WithEnvironmentValue_adds_a_variable_the_plist_lacks() {
        var plist = EnvPlist(new() { ["KCAP_PROFILE"] = "work" });

        var rewritten = LaunchdUnit.WithEnvironmentValue(plist, "PATH", "/usr/bin");

        await Assert.That(LaunchdPrint.Environment(rewritten!)["PATH"]).IsEqualTo("/usr/bin");
        await Assert.That(LaunchdPrint.Environment(rewritten!)["KCAP_PROFILE"]).IsEqualTo("work");
    }

    [Test]
    public async Task WithEnvironmentValue_is_null_when_the_value_is_already_set() {
        var plist = EnvPlist(new() { ["PATH"] = "/usr/bin" });

        await Assert.That(LaunchdUnit.WithEnvironmentValue(plist, "PATH", "/usr/bin")).IsNull();
    }
}
