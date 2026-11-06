using System.Xml.Linq;

namespace Capacitor.Cli.Core.Setup;

/// <summary>Readers for <c>launchctl print</c> output and for launchd plists.</summary>
public static class LaunchdPrint {
    /// <summary>The program <c>launchctl print</c> shows the loaded job running, or null when it shows none.
    /// launchd reads the plist only when the job loads, so this lags a rewritten plist until a reload.</summary>
    public static string? LoadedProgram(string printStdout) {
        foreach (var line in printStdout.Split('\n')) {
            var t = line.Trim();
            if (t.StartsWith("program = ", StringComparison.Ordinal)) return t["program = ".Length..];
        }
        return null;
    }

    /// <summary>Strict key/value walk: throws on any malformed key/value alternation.</summary>
    static IEnumerable<(string Key, XElement Value)> KeyedElements(XElement dict, string context) {
        string? pendingKey = null;
        foreach (var el in dict.Elements()) {
            if (el.Name == "key") {
                if (pendingKey is not null)
                    throw new InvalidDataException($"{context}consecutive <key> nodes ('{pendingKey}', '{el.Value}') in plist");
                pendingKey = el.Value;
                continue;
            }

            if (pendingKey is null)
                throw new InvalidDataException($"{context}value with no preceding key in plist");

            yield return (pendingKey, el);
            pendingKey = null;
        }

        if (pendingKey is not null)
            throw new InvalidDataException($"{context}dangling <key>{pendingKey}</key> with no value in plist");
    }

    /// <summary>The element paired with the top-level <c>&lt;key&gt;</c> named
    /// <paramref name="key"/>, or null when that key never appears; a duplicate throws.</summary>
    public static XElement? TopLevelValue(XElement dict, string key) {
        XElement? result = null;
        var found = false;
        foreach (var (k, value) in KeyedElements(dict, "")) {
            if (k != key) continue;
            if (found) throw new InvalidDataException($"duplicate {key} key in plist");
            found  = true;
            result = value;
        }
        return result;
    }

    /// <summary>
    /// The daemon binary baked into a plist — the first <c>&lt;string&gt;</c> of the
    /// <c>&lt;array&gt;</c> paired with the top-level <c>&lt;key&gt;ProgramArguments&lt;/key&gt;</c>
    /// (see <see cref="TopLevelValue"/>). Used by <c>daemon doctor</c> to detect a moved binary.
    /// </summary>
    public static string? Binary(string plistXml) {
        var topDict = XDocument.Parse(plistXml).Root?.Element("dict");
        if (topDict is null) return null;

        var programArguments = TopLevelValue(topDict, "ProgramArguments");
        return programArguments?.Name == "array" ? programArguments.Elements("string").FirstOrDefault()?.Value : null;
    }

    /// <summary>The label a plist declares, which launchd loads it under whatever the file is called; null when it
    /// declares none. Walked as <see cref="Binary"/> is.</summary>
    public static string? Label(string plistXml) {
        var topDict = XDocument.Parse(plistXml).Root?.Element("dict");
        var label   = topDict is null ? null : TopLevelValue(topDict, "Label");
        return label?.Name == "string" ? label.Value : null;
    }

    /// <summary>
    /// The environment baked into a plist — the dict paired with the top-level
    /// <c>EnvironmentVariables</c> key, walked strictly (see <see cref="KeyedElements"/>). Empty
    /// only when the block is genuinely absent; any other malformed shape throws
    /// <see cref="InvalidDataException"/> rather than degrading silently — this file is never
    /// hand-edited, so a gate caller reading identity evidence here must see ambiguous evidence
    /// as unreadable, never guessed.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment(string plistXml) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var topDict = XDocument.Parse(plistXml).Root?.Element("dict");
        if (topDict is null) return result;

        var envDict = TopLevelValue(topDict, "EnvironmentVariables");
        if (envDict is null) return result; // genuinely absent — a legitimate "no baked env" shape

        if (envDict.Name != "dict")
            throw new InvalidDataException("EnvironmentVariables is not a dict in plist");

        foreach (var (key, value) in KeyedElements(envDict, "EnvironmentVariables has ")) {
            if (value.Name != "string")
                throw new InvalidDataException($"EnvironmentVariables key '{key}' is paired with a non-string value in plist");
            if (!result.TryAdd(key, value.Value))
                throw new InvalidDataException($"duplicate EnvironmentVariables key '{key}' in plist");
        }

        return result;
    }

    public static LabelProbe Classify(int exitCode, string stdout, string stderr) {
        if (exitCode == 0) return LabelProbe.Loaded;
        return stderr.Contains("Could not find service", StringComparison.OrdinalIgnoreCase)
            ? LabelProbe.Absent
            : LabelProbe.Unknown;
    }

    public static int? Pid(string stdout) {
        foreach (var line in stdout.Split('\n')) {
            var t = line.Trim();
            if (t.StartsWith("pid = ", StringComparison.Ordinal) && int.TryParse(t["pid = ".Length..].Trim(), out var pid))
                return pid;
        }
        return null;
    }
}
