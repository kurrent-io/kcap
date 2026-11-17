using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.RepoEvidence;

/// <summary>
/// Finds a <c>gh pr create</c> a shell would run in a command line. Quoted text, heredoc bodies and comments are not
/// commands, so a line that only mentions one — an echo, a PR body describing it — does not match. Chains
/// (<c>cd … &amp;&amp; gh pr create</c>), command substitutions and <c>sh -c</c> scripts are searched too.
/// </summary>
public static partial class PrCreateCommand {
    const int MaxDepth = 4;

    static readonly HashSet<string> Prefixes = new(StringComparer.Ordinal) { "env", "command", "exec", "time", "nohup", "sudo", "{", "!" };
    static readonly HashSet<string> Shells   = new(StringComparer.Ordinal) { "sh", "bash", "zsh", "dash", "ksh" };

    /// <summary>True when <paramref name="command"/> runs <c>gh pr create</c>; <paramref name="repo"/> is its
    /// <c>--repo</c>/<c>-R</c> value, if it named one. A <c>--dry-run</c> creates nothing and does not match.</summary>
    public static bool TryMatch(string command, out string? repo) {
        repo = null;
        if (!command.Contains("gh", StringComparison.Ordinal) || !command.Contains("create", StringComparison.Ordinal)) return false;

        var found = false;
        Search(StripHeredocBodies(command), 0, ref found, ref repo);
        return found;
    }

    /// <summary>Whether a <c>--repo</c> value names <paramref name="owner"/>/<paramref name="name"/>. It may be
    /// <c>owner/repo</c>, <c>host/owner/repo</c> or a URL.</summary>
    public static bool RepoMatches(string repo, string owner, string name) {
        var value = repo.Trim().TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];

        var segments = value.Split('/');

        return segments.Length >= 2
            && string.Equals(segments[^2], owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[^1], name, StringComparison.OrdinalIgnoreCase);
    }

    static void Search(string text, int depth, ref bool found, ref string? repo) {
        var commands = new List<List<string>>();
        var scripts  = new List<string>();
        Split(text, commands, scripts);

        foreach (var words in commands) {
            if (Inspect(words, out var script, out var invocationRepo)) {
                found = true;
                repo  = invocationRepo;
            } else if (script is not null) {
                scripts.Add(script);
            }
        }

        if (depth >= MaxDepth) return;

        foreach (var script in scripts) Search(script, depth + 1, ref found, ref repo);
    }

    static bool Inspect(List<string> words, out string? script, out string? repo) {
        script = null;
        repo   = null;

        var i = 0;
        while (i < words.Count && (Prefixes.Contains(words[i]) || Assignment().IsMatch(words[i]))) i++;
        if (i >= words.Count) return false;

        var exe = Path.GetFileName(words[i]);

        if (Shells.Contains(exe)) {
            for (var j = i + 1; j < words.Count - 1; j++) {
                var flag = words[j];
                if (!flag.StartsWith('-') || flag.StartsWith("--", StringComparison.Ordinal) || !flag.Contains('c')) continue;

                script = words[j + 1];
                break;
            }

            return false;
        }

        if (exe != "gh" || words.Count < i + 3 || words[i + 1] != "pr" || words[i + 2] != "create") return false;

        for (var j = i + 3; j < words.Count; j++) {
            var arg = words[j];

            if (arg == "--dry-run") return false;

            if (arg is "--repo" or "-R") {
                if (j + 1 < words.Count) repo = words[++j];
            } else if (arg.StartsWith("--repo=", StringComparison.Ordinal)) {
                repo = arg["--repo=".Length..];
            } else if (arg.StartsWith("-R", StringComparison.Ordinal) && arg.Length > 2) {
                repo = arg[2..];
            }
        }

        return true;
    }

    /// <summary>Drops every heredoc's body, which is data however it is quoted. It runs before splitting because a body
    /// routinely holds an unbalanced quote, such as an apostrophe in a PR description.</summary>
    static string StripHeredocBodies(string text) {
        if (!text.Contains("<<", StringComparison.Ordinal)) return text;

        var kept       = new StringBuilder(text.Length);
        var delimiters = new Queue<(string Word, bool StripTabs)>();

        foreach (var raw in text.Split('\n')) {
            var line = raw.TrimEnd('\r');

            if (delimiters.Count > 0) {
                var (word, stripTabs) = delimiters.Peek();
                if ((stripTabs ? line.TrimStart('\t') : line) == word) delimiters.Dequeue();
                continue;
            }

            kept.Append(raw).Append('\n');

            foreach (Match match in Heredoc().Matches(line)) {
                delimiters.Enqueue((match.Groups["word"].Value, match.Groups["dash"].Success));
            }
        }

        return kept.ToString();
    }

    /// <summary>Splits shell text into simple commands of words. A command substitution's body becomes opaque word
    /// text here and is handed back in <paramref name="scripts"/>, since it runs too.</summary>
    static void Split(string text, List<List<string>> commands, List<string> scripts) {
        var words  = new List<string>();
        var word   = new StringBuilder();
        var inWord = false;
        var i      = 0;

        while (i < text.Length) {
            var c = text[i];

            switch (c) {
                case ' ' or '\t' or '\r':
                    EndWord();
                    i++;
                    break;
                case '\n' or ';' or '|' or '&' or '(' or ')':
                    EndCommand();
                    i++;
                    break;
                case '#' when !inWord:
                    while (i < text.Length && text[i] != '\n') i++;
                    break;
                case '\\':
                    if (i + 1 < text.Length && text[i + 1] != '\n') word.Append(text[i + 1]);
                    inWord =  true;
                    i      += 2;
                    break;
                case '\'': {
                    var end = text.IndexOf('\'', i + 1);
                    if (end < 0) end = text.Length;
                    word.Append(text, i + 1, end - i - 1);
                    inWord = true;
                    i      = end + 1;
                    break;
                }
                case '"':
                    i      = ReadDoubleQuoted(text, i + 1, word, scripts);
                    inWord = true;
                    break;
                case '$' when i + 1 < text.Length && text[i + 1] == '(':
                    i      = ReadSubstitution(text, i + 2, scripts);
                    inWord = true;
                    break;
                case '$' when i + 1 < text.Length && text[i + 1] == '{': {
                    var end = text.IndexOf('}', i + 2);
                    i      = end < 0 ? text.Length : end + 1;
                    inWord = true;
                    break;
                }
                case '`':
                    i      = ReadBackticks(text, i + 1, scripts);
                    inWord = true;
                    break;
                case '>' or '<':
                    word.Append(c);
                    inWord = true;
                    i++;
                    if (i < text.Length && text[i] == '&') {
                        word.Append('&');
                        i++;
                    }
                    break;
                default:
                    word.Append(c);
                    inWord = true;
                    i++;
                    break;
            }
        }

        EndCommand();
        return;

        void EndWord() {
            if (!inWord) return;

            words.Add(word.ToString());
            word.Clear();
            inWord = false;
        }

        void EndCommand() {
            EndWord();
            if (words.Count == 0) return;

            commands.Add(words);
            words = [];
        }
    }

    static int ReadDoubleQuoted(string text, int i, StringBuilder word, List<string> scripts) {
        while (i < text.Length) {
            var c = text[i];

            if (c == '"') return i + 1;

            if (c == '\\' && i + 1 < text.Length) {
                word.Append(text[i + 1]);
                i += 2;
            } else if (c == '$' && i + 1 < text.Length && text[i + 1] == '(') {
                i = ReadSubstitution(text, i + 2, scripts);
            } else if (c == '`') {
                i = ReadBackticks(text, i + 1, scripts);
            } else {
                word.Append(c);
                i++;
            }
        }

        return i;
    }

    static int ReadBackticks(string text, int start, List<string> scripts) {
        var end = text.IndexOf('`', start);
        if (end < 0) end = text.Length;

        scripts.Add(text[start..end]);
        return end + 1;
    }

    /// <summary>Reads a <c>$(…)</c> body from just inside it and returns the index past its closing paren.</summary>
    static int ReadSubstitution(string text, int start, List<string> scripts) {
        var depth = 1;
        var i     = start;

        while (i < text.Length) {
            var c = text[i];

            switch (c) {
                case '\\':
                    i += 2;
                    continue;
                case '\'': {
                    var end = text.IndexOf('\'', i + 1);
                    i = end < 0 ? text.Length : end + 1;
                    continue;
                }
                case '"':
                    i = ReadDoubleQuoted(text, i + 1, new StringBuilder(), []);
                    continue;
                case '(':
                    depth++;
                    break;
                case ')' when --depth == 0:
                    scripts.Add(text[start..i]);
                    return i + 1;
            }

            i++;
        }

        scripts.Add(text[start..]);
        return text.Length;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex Assignment();

    [GeneratedRegex("""(?<!<)<<(?<dash>-)?\s*(?<q>['"]?)(?<word>[A-Za-z_][A-Za-z0-9_]*)\k<q>""")]
    private static partial Regex Heredoc();
}
