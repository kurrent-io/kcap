using System.Text;
using System.Xml.Linq;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Services;

/// <summary>Pure rendering + command vectors for a per-user launchd LaunchAgent.</summary>
static class LaunchdUnit {
    const string LabelPrefix = "io.kurrent.kcap.daemon.";

    public static string Label(string id) => LabelPrefix + id;

    // Adaptive lets macOS hold the job at background priority 4, and under load that starves the hub
    // heartbeat into a reconnect storm. Background does the same.
    const string StandardProcessTypeLine = "  <key>ProcessType</key><string>Standard</string>\n";
    const string AdaptiveValue   = "Adaptive";
    const string BackgroundValue = "Background";

    /// <summary>The plist with its top-level <c>ProcessType</c> value switched from Adaptive or Background to
    /// Standard, every other byte intact; null when there is nothing to splice — no such key, a value that
    /// is not one of those two, a value with markup inside it, or a document that does not parse. Null is
    /// not a verdict on the plist: the caller validates <c>upgraded ?? original</c> itself.</summary>
    public static string? UpgradeProcessType(string plistXml) {
        XDocument doc;
        try { doc = XDocument.Parse(plistXml, LoadOptions.SetLineInfo); }
        catch (System.Xml.XmlException) { return null; }

        var topDict = doc.Root?.Element("dict");
        if (topDict is null) return null;

        XElement? value;
        try { value = LaunchdPrint.TopLevelValue(topDict, "ProcessType"); }
        catch (InvalidDataException) { return null; }
        if (value is null || value.Name != "string") return null;

        // Exactly one plain text node: a comment or CDATA inside the element is not a value this
        // writer produced, and the byte check below would not describe it.
        if (value.FirstNode is not XText text || text is XCData || text.NextNode is not null) return null;
        if (text.Value is not (AdaptiveValue or BackgroundValue)) return null;

        // LinePosition is the column of the element name's first character; the '<' is one before.
        var info = (System.Xml.IXmlLineInfo)value;
        if (!info.HasLineInfo()) return null;
        var start = OffsetOf(plistXml, info.LineNumber, info.LinePosition - 1);
        if (start < 0) return null;

        var element = $"<string>{text.Value}</string>";
        if (start + element.Length > plistXml.Length || string.CompareOrdinal(plistXml, start, element, 0, element.Length) != 0) return null;

        var valueStart = start + "<string>".Length;
        var rewritten  = string.Concat(plistXml.AsSpan(0, valueStart), "Standard", plistXml.AsSpan(valueStart + text.Value.Length));
        return DeclaresStandardProcessType(rewritten) ? rewritten : null;
    }

    /// <summary>Character offset of a 1-based line and column, walking the source's own line endings so
    /// LF and CRLF documents both map correctly; -1 when the position is outside the text.</summary>
    static int OffsetOf(string source, int line, int column) {
        var offset = 0;
        for (var current = 1; current < line; current++) {
            var newline = source.IndexOf('\n', offset);
            if (newline < 0) return -1;
            offset = newline + 1;
        }
        var position = offset + column - 1;
        return position >= 0 && position <= source.Length ? position : -1;
    }

    /// <summary>True when the plist's top-level <c>ProcessType</c> is Standard, however it is formatted.
    /// An unparseable plist is not.</summary>
    public static bool DeclaresStandardProcessType(string plistXml) {
        try {
            var topDict = XDocument.Parse(plistXml).Root?.Element("dict");
            return topDict is not null && LaunchdPrint.TopLevelValue(topDict, "ProcessType") is { Name.LocalName: "string", Value: "Standard" };
        } catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException) {
            return false;
        }
    }

    /// <summary>The word on <c>launchctl print</c>'s <c>spawn type = &lt;word&gt; (&lt;n&gt;)</c> line, lower-cased,
    /// or null when the print has no such line. launchd reads the plist only when the job loads, so this
    /// lags a rewritten plist until a reload.</summary>
    public static string? LoadedSpawnType(string printStdout) {
        foreach (var line in printStdout.Split('\n')) {
            var t = line.Trim();
            if (!t.StartsWith("spawn type = ", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = t["spawn type = ".Length..].Trim();
            var end  = rest.IndexOf(' ');
            var word = (end < 0 ? rest : rest[..end]).Trim();
            return word.Length == 0 ? null : word.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// The plist with its daemon binary (the first <c>ProgramArguments</c> entry) replaced by
    /// <paramref name="binary"/>, or null when the binary is already that or the plist is not shaped the
    /// way this writer writes it. Text-level, so every other byte of the unit survives.
    /// </summary>
    public static string? WithBinary(string plistXml, string binary) {
        string? current;
        try { current = LaunchdPrint.Binary(plistXml); }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException) { return null; }

        if (current is null || current == binary) return null;

        const string key = "<key>ProgramArguments</key><array>";
        var at = plistXml.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return null;

        var oldElement = $"<string>{ServiceText.Xml(current)}</string>";
        var start      = plistXml.IndexOf("<string>", at + key.Length, StringComparison.Ordinal);
        if (start < 0 || string.CompareOrdinal(plistXml, start, oldElement, 0, oldElement.Length) != 0) return null;

        var rewritten = string.Concat(plistXml.AsSpan(0, start), $"<string>{Guarded("binary path", binary)}</string>",
            plistXml.AsSpan(start + oldElement.Length));

        return LaunchdPrint.Binary(rewritten) == binary ? rewritten : null;
    }

    /// <summary>
    /// The plist with <paramref name="key"/> in its <c>EnvironmentVariables</c> set to <paramref name="value"/>,
    /// or null when it already is or the plist is not shaped the way this writer writes it. Text-level, like
    /// <see cref="WithBinary"/>.
    /// </summary>
    public static string? WithEnvironmentValue(string plistXml, string key, string value) {
        IReadOnlyDictionary<string, string> before;
        try { before = LaunchdPrint.Environment(plistXml); }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException) { return null; }

        if (before.TryGetValue(key, out var current) && current == value) return null;

        const string open = "<key>EnvironmentVariables</key><dict>";
        var start = plistXml.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        var end = plistXml.IndexOf("</dict>", start, StringComparison.Ordinal);
        if (end < 0) return null;

        ServiceText.RequireValidEnvName(key);
        ServiceText.RequireXmlRepresentableValue(key, value);
        var element = $"<key>{ServiceText.Xml(key)}</key><string>{ServiceText.Xml(value)}</string>";

        string rewritten;
        if (current is null) {
            rewritten = string.Concat(plistXml.AsSpan(0, start), $"\n    {element}", plistXml.AsSpan(start));
        } else {
            var oldElement = $"<key>{ServiceText.Xml(key)}</key><string>{ServiceText.Xml(current)}</string>";
            var at = plistXml.IndexOf(oldElement, start, end - start, StringComparison.Ordinal);
            if (at < 0) return null;
            rewritten = string.Concat(plistXml.AsSpan(0, at), element, plistXml.AsSpan(at + oldElement.Length));
        }

        var after = LaunchdPrint.Environment(rewritten);
        var others = before.Where(kv => kv.Key != key);

        return after.TryGetValue(key, out var written) && written == value && after.Count == before.Count + (current is null ? 1 : 0)
            && others.All(kv => after.TryGetValue(kv.Key, out var v) && v == kv.Value)
            ? rewritten
            : null;
    }

    /// <summary>The Library/LaunchAgents directory under the given home.</summary>
    public static string AgentsDir(UserHome home) =>
        Path.Combine(home.Path, "Library", "LaunchAgents");

    public static string PlistPath(UserHome home, string id) =>
        Path.Combine(AgentsDir(home), Label(id) + ".plist");

    /// <summary>Separate stdout/stderr capture file (keeps the rolling --log-file uncluttered).</summary>
    static string OutLogPath(ServiceSpec spec) =>
        Path.ChangeExtension(spec.LogPath, null) + ".out.log";

    public static string Plist(ServiceSpec spec) {
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>

            """);
        sb.Append($"  <key>Label</key><string>{Guarded("the service label", Label(spec.ServiceId))}</string>\n");

        sb.Append("  <key>ProgramArguments</key><array>\n");
        foreach (var arg in ProgramArguments(spec))
            sb.Append($"    <string>{Guarded("the daemon command line", arg)}</string>\n");
        sb.Append("  </array>\n");

        sb.Append("  <key>EnvironmentVariables</key><dict>\n");
        foreach (var (k, v) in spec.Environment) {
            // XML escaping already contains the name structurally, but a control character is not legal
            // XML 1.0 at all — so the plist would be silently unparseable rather than injected. Same
            // check, same reason: one grammar, applied at every sink.
            ServiceText.RequireValidEnvName(k);
            ServiceText.RequireXmlRepresentableValue(k, v);
            sb.Append($"    <key>{ServiceText.Xml(k)}</key><string>{ServiceText.Xml(v)}</string>\n");
        }
        sb.Append("  </dict>\n");

        sb.Append("  <key>RunAtLoad</key><true/>\n");
        sb.Append("  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>\n");
        sb.Append(StandardProcessTypeLine);
        var outLog = Guarded("the daemon log path", OutLogPath(spec));
        sb.Append($"  <key>StandardOutPath</key><string>{outLog}</string>\n");
        sb.Append($"  <key>StandardErrorPath</key><string>{outLog}</string>\n");
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Guard, then escape — every string this writer interpolates goes through here.
    ///
    /// <para><b>Why not just escape.</b> <c>SecurityElement.Escape</c> handles the five markup characters and
    /// nothing else; it passes a C0 control, U+FFFE or a lone surrogate straight through, and none of those
    /// has any legal XML 1.0 representation. The result is a plist <c>launchctl</c> silently will not load.</para>
    ///
    /// <para><b>Why it covers the paths and not only the environment.</b> The environment loop was guarded and
    /// these four sites were not, yet <c>ProgramArguments</c> carries the binary path, the log path and the
    /// extra args, and macOS filenames may contain any byte but <c>/</c> and NUL. Guarding the environment
    /// only is the same asymmetry review already found twice at the other two sinks: applying a check to some
    /// of the values interpolated into a file is not checking the file.</para>
    /// </summary>
    static string Guarded(string what, string value) {
        ServiceText.RequireXmlRepresentableValue(what, value);
        return ServiceText.Xml(value);
    }

    /// <summary>argv the agent runs: binary, pinned --name + --log-file, then extra args.</summary>
    public static IReadOnlyList<string> ProgramArguments(ServiceSpec spec) =>
        [spec.DaemonBinaryPath, "--name", spec.ServiceId, "--log-file", spec.LogPath, .. spec.ExtraArgs];

    public static string? IdFromPlistFileName(string fileName) {
        var name = Path.GetFileNameWithoutExtension(fileName); // strips .plist
        return name.StartsWith(LabelPrefix, StringComparison.Ordinal)
            ? name[LabelPrefix.Length..]
            : null;
    }

    /// <summary>Distinguishes genuine absence from present-but-unreadable evidence.</summary>
    internal enum PlistRead { Absent, Ok, Unreadable }

    /// <summary>Discriminated read: a not-found blocked by structural evidence (a link at the exact
    /// path, or a file/link ancestor) is <see cref="PlistRead.Unreadable"/>, never <see cref="PlistRead.Absent"/>.</summary>
    internal static PlistRead TryReadPlist(string path, out string? content) {
        try {
            content = File.ReadAllText(path);
            return PlistRead.Ok;
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            content = null;
            return PathEvidence.PathBlockedByFileOrLink(path) ? PlistRead.Unreadable : PlistRead.Absent;
        } catch {
            content = null;
            return PlistRead.Unreadable;
        }
    }

    // ── command vectors (uid passed in so these stay pure) ──
    public static string[] BootstrapArgs(int uid, string plistPath) => ["bootstrap", $"gui/{uid}", plistPath];
    public static string[] BootoutArgs(int uid, string id)          => ["bootout", $"gui/{uid}/{Label(id)}"];
    public static string[] KickstartArgs(int uid, string id)        => ["kickstart", $"gui/{uid}/{Label(id)}"];
    public static string[] KillArgs(int uid, string id)             => ["kill", "SIGTERM", $"gui/{uid}/{Label(id)}"];
    public static string[] PrintArgs(int uid, string id)            => ["print", $"gui/{uid}/{Label(id)}"];

    public static ServiceState StatusFromPrint(int exitCode, string stdout) {
        if (exitCode != 0) return ServiceState.NotInstalled;
        return stdout.Contains("state = running", StringComparison.OrdinalIgnoreCase)
            ? ServiceState.Running
            : ServiceState.Installed;
    }

    /// <summary>launchctl's stderr without its "re-run as root" hint: the job is a per-user agent, and
    /// running kcap as root would target root's own domain instead.</summary>
    public static string ToolError(string stderr) =>
        string.Join(' ', stderr.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith("Try re-running the command as root", StringComparison.OrdinalIgnoreCase)));
}
