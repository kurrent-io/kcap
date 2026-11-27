using System.Text;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>An app-server command as the script it runs. Codex reports a shell call as the argv it spawns, quoted and
/// joined into one line — <c>/bin/bash -lc 'touch x'</c>. Only a line that splits back into exactly
/// <c>&lt;shell&gt; -c|-lc &lt;script&gt;</c> is peeled: anything the joiner would not have written is returned as it
/// came, so what a person approves is never a guess at what runs.</summary>
internal static class CodexShellCommand {
    static readonly HashSet<string> Shells = new(StringComparer.Ordinal) { "bash", "zsh", "sh" };

    public static string Script(string command) =>
        Words(command) is [var shell, "-c" or "-lc", { Length: > 0 } script] && Shells.Contains(shell[(shell.LastIndexOf('/') + 1)..])
            ? script
            : command;

    /// <summary>Null unless every word is built the way the joiner quotes one: bare characters no shell treats
    /// specially, single-quoted runs, and double-quoted runs escaping only <c>$ ` " \</c>.</summary>
    static List<string>? Words(string line) {
        var words  = new List<string>();
        var word   = new StringBuilder();
        var inWord = false;

        for (var i = 0; i < line.Length; i++) {
            switch (line[i]) {
                case ' ':
                    if (inWord) words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                    continue;
                case '\'':
                    var close = line.IndexOf('\'', i + 1);
                    if (close < 0) return null;
                    word.Append(line, i + 1, close - i - 1);
                    i = close;
                    break;
                case '"':
                    for (i++; ; i++) {
                        if (i >= line.Length || line[i] is '$' or '`') return null;
                        if (line[i] == '"') break;
                        if (line[i] == '\\') {
                            if (i + 1 >= line.Length || line[i + 1] is not ('$' or '`' or '"' or '\\')) return null;
                            i++;
                        }
                        word.Append(line[i]);
                    }
                    break;
                case var c when IsBare(c):
                    word.Append(c);
                    break;
                default:
                    return null;
            }
            inWord = true;
        }

        if (inWord) words.Add(word.ToString());
        return words;
    }

    static bool IsBare(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.' or '/' or ':' or '@' or '_' or ']' or ',' or '=' or '%' || c > '\x7f';
}
