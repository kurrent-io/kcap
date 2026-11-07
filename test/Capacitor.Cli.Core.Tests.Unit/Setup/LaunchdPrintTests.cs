using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class LaunchdPrintTests {
    [Test]
    public async Task Classify_zero_exit_is_loaded() {
        await Assert.That(LaunchdPrint.Classify(0, "state = running\npid = 924\n", "")).IsEqualTo(LabelProbe.Loaded);
    }

    [Test]
    public async Task Classify_could_not_find_is_absent() {
        await Assert.That(LaunchdPrint.Classify(113, "", "Could not find service \"io.kurrent.kcap.daemon.x\" in domain for user gui: 501"))
            .IsEqualTo(LabelProbe.Absent);
    }

    [Test]
    public async Task Classify_nonzero_without_not_found_signature_is_unknown() {
        await Assert.That(LaunchdPrint.Classify(1, "", "Operation not permitted")).IsEqualTo(LabelProbe.Unknown);
    }

    [Test]
    public async Task Pid_parsed_from_print() {
        await Assert.That(LaunchdPrint.Pid("\tstate = running\n\tpid = 924\n")).IsEqualTo(924);
    }

    [Test]
    public async Task Pid_null_when_absent() {
        await Assert.That(LaunchdPrint.Pid("\tstate = waiting\n")).IsNull();
    }

    [Test]
    public async Task LoadedProgram_reads_the_print_line() {
        const string program = "/home/u/.local/share/kcap/versions/1.0.0/bin/kcap-daemon";
        await Assert.That(LaunchdPrint.LoadedProgram($"io.kurrent.kcap.daemon.test = {{\n\tprogram = {program}\n}}")).IsEqualTo(program);
        await Assert.That(LaunchdPrint.LoadedProgram("")).IsNull();
    }

    /// <summary>A decoy <c>&lt;array&gt;</c> planted before the real <c>ProgramArguments</c> must
    /// never be read as the binary — <see cref="LaunchdPrint.Binary"/> pairs the array with
    /// its OWN preceding <c>&lt;key&gt;ProgramArguments&lt;/key&gt;</c>, not "the document's first
    /// array". A foreign writer that planted such an array would pass the digest gate
    /// while launchd actually executes the later, real ProgramArguments.</summary>
    [Test]
    public async Task Binary_ignores_a_decoy_array_before_the_real_ProgramArguments() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.decoy</string>
              <key>Decoy</key><array>
                <string>/bin/evil-daemon</string>
              </array>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
            </dict>
            </plist>
            """;
        await Assert.That(LaunchdPrint.Binary(xml)).IsEqualTo("/bin/kcap-daemon");
    }

    [Test]
    public async Task Binary_throws_on_a_duplicate_ProgramArguments_key() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.dup</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>ProgramArguments</key><array>
                <string>/bin/evil-daemon</string>
              </array>
            </dict>
            </plist>
            """;
        await Assert.That(() => LaunchdPrint.Binary(xml)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Environment_throws_on_a_duplicate_top_level_EnvironmentVariables_key() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.dup</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>EnvironmentVariables</key><dict>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><string>prompt</string>
              </dict>
              <key>EnvironmentVariables</key><dict>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><string>allow</string>
              </dict>
            </dict>
            </plist>
            """;
        await Assert.That(() => LaunchdPrint.Environment(xml)).Throws<InvalidDataException>();
    }

    /// <summary>This file is never hand-edited by the daemon's plist writer — a duplicate
    /// key can only mean a foreign/corrupt writer. Last-win would let a gate caller silently trust
    /// whichever value happened to land last; throwing forces every caller's existing "unreadable
    /// evidence" containment to see it instead.</summary>
    [Test]
    public async Task Environment_throws_on_a_duplicate_key_rather_than_last_win() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.dup</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>EnvironmentVariables</key><dict>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><string>prompt</string>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><string>allow</string>
              </dict>
            </dict>
            </plist>
            """;
        await Assert.That(() => LaunchdPrint.Environment(xml)).Throws<InvalidDataException>();
    }

    // ── EnvFromPlist: malformed keyed-structure shapes that must throw rather than silently
    // degrade (empty map / skipped pair / dodged duplicate detection). ──

    [Test]
    public async Task Environment_throws_when_EnvironmentVariables_is_paired_with_a_non_dict() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.bad</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>EnvironmentVariables</key><array>
                <string>not a dict at all</string>
              </array>
            </dict>
            </plist>
            """;
        // A non-dict pairing is ambiguous/malformed evidence, not "no env vars" — it must throw
        // rather than silently return an empty map.
        await Assert.That(() => LaunchdPrint.Environment(xml)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Environment_throws_when_a_key_is_paired_with_a_non_string_value() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.bad</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>EnvironmentVariables</key><dict>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><integer>1</integer>
              </dict>
            </dict>
            </plist>
            """;
        // A relevant key paired with the wrong value type is malformed evidence, not absence — it
        // must throw rather than silently skip the pair.
        await Assert.That(() => LaunchdPrint.Environment(xml)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Environment_throws_on_consecutive_key_nodes_rather_than_silently_overwriting() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.bad</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>EnvironmentVariables</key><dict>
                <key>KCAP_CONSENT_SEED_DEFAULT</key>
                <key>KCAP_PROFILE</key><string>work</string>
              </dict>
            </dict>
            </plist>
            """;
        // The second <key> must not silently overwrite the pending key — that would drop
        // KCAP_CONSENT_SEED_DEFAULT entirely and dodge duplicate-key detection for whatever key it
        // collided with.
        await Assert.That(() => LaunchdPrint.Environment(xml)).Throws<InvalidDataException>();
    }

    // ── TopLevelValue: the same strict key/value alternation as EnvFromPlist's inner walk above,
    // enforced at the OUTER (top-level dict) level. ──

    [Test]
    public async Task Environment_throws_on_two_consecutive_identical_top_level_keys_followed_by_one_value() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.dup</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>EnvironmentVariables</key>
              <key>EnvironmentVariables</key><dict>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><string>prompt</string>
              </dict>
            </dict>
            </plist>
            """;
        // Must still throw — pairing the lone value with whichever key landed last would evade
        // duplicate-key detection.
        await Assert.That(() => LaunchdPrint.Environment(xml)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Environment_throws_when_the_EnvironmentVariables_key_is_immediately_followed_by_another_key() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.dup</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>EnvironmentVariables</key>
              <key>Decoy</key><dict>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><string>prompt</string>
              </dict>
            </dict>
            </plist>
            """;
        // Pairing the dict with the LATER key instead would make EnvironmentVariables read as
        // absent — the takeover-safe outcome a gate caller must never see for a malformed plist.
        await Assert.That(() => LaunchdPrint.Environment(xml)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Binary_throws_on_a_top_level_value_with_no_preceding_key() {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <string>orphan value with no preceding key</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
            </dict>
            </plist>
            """;
        await Assert.That(() => LaunchdPrint.Binary(xml)).Throws<InvalidDataException>();
    }
}
