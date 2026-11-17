using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.RepoEvidence;

/// <summary>
/// Finds a <c>gh pr create</c> a shell would run in a command line, and whose output is the line's output. Quoted
/// text, heredoc bodies and comments are not commands, so a line that only mentions one — an echo, a PR body
/// describing it — does not match. A create must be the last command run, and one no <c>||</c> can skip: after
/// <c>gh pr create || gh pr view</c> the output and exit status are the view's, which prints an existing PR's URL.
/// A substitution holding one must be the whole of a variable assignment followed only by an echo of that variable,
/// joined by <c>&amp;&amp;</c> (<c>url=$(gh pr create …) &amp;&amp; echo "$url"</c>), so its URL reaches the output.
/// The one thing that may follow a create is a pipe into a <c>tail</c> or <c>head</c> keeping two lines or more
/// (<c>gh pr create … 2&gt;&amp;1 | tail -5</c>), which agents use to trim output; the run's exit status is then the
/// filter's, which the match reports as filtered. Such a create must keep its stderr, where gh says a PR already
/// exists, and follow nothing but a <c>cd</c>.
/// </summary>
public static partial class PrCreateCommand {
    const int MaxDepth = 4;

    static readonly HashSet<string> Prefixes = new(StringComparer.Ordinal) { "env", "command", "exec", "time", "nohup", "sudo", "{" };
    static readonly HashSet<string> Shells   = new(StringComparer.Ordinal) { "sh", "bash", "zsh", "dash", "ksh" };

    static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal) {
        "--title", "-t", "--body", "-b", "--body-file", "-F", "--base", "-B", "--head", "-H", "--reviewer", "-r",
        "--assignee", "-a", "--label", "-l", "--project", "-p", "--milestone", "-m", "--template", "-T", "--recover",
    };

    /// <summary>True when <paramref name="command"/> runs <c>gh pr create</c> as its last command;
    /// <paramref name="repo"/> is its <c>--repo</c>/<c>-R</c> value, if it named one. A <c>--dry-run</c> creates
    /// nothing and does not match.</summary>
    public static bool TryMatch(string command, out string? repo) => TryMatch(command, out repo, out _);

    /// <inheritdoc cref="TryMatch(string, out string?)"/>
    /// <param name="filtered">The create's output passes through a <c>tail</c> or <c>head</c>, whose exit status the run
    /// reports in place of the create's.</param>
    public static bool TryMatch(string command, out string? repo, out bool filtered) {
        repo     = null;
        filtered = false;
        return command.Contains("gh", StringComparison.Ordinal)
            && command.Contains("create", StringComparison.Ordinal)
            && Search(command, 0, out repo, out filtered);
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

    static bool Search(string text, int depth, out string? repo, out bool filtered) {
        repo     = null;
        filtered = false;

        var commands = new List<ShellCommand>();
        Scan(text, 0, substitution: false, commands);

        for (var k = commands.Count - 1; k >= 0; k--) {
            var command = commands[k];

            if (Inspect(command, out var shellScript, out var invocationRepo)) {
                filtered = k == commands.Count - 2 && IsLineFilter(commands[k + 1]);
                if (k != commands.Count - 1 && !filtered || !RunsUnconditionally(commands, k)) return false;
                if (filtered && (command.DiscardsStderr || !OnlyChangesDirectory(commands, k))) return false;

                repo = invocationRepo;
                return true;
            }

            if (depth >= MaxDepth) continue;

            var wrapsShell = shellScript is not null;
            if (wrapsShell) command.Scripts.Add(shellScript!);

            foreach (var script in command.Scripts) {
                if (!Search(script, depth + 1, out var nestedRepo, out var nestedFiltered)) continue;
                var accepted = command.Scripts.Count == 1
                            && RunsUnconditionally(commands, k)
                            && (wrapsShell && k == commands.Count - 1 && !command.RedirectsStdout || k == commands.Count - 2 && EchoesAssignment(command, commands[k + 1]));
                if (!accepted) return false;

                if (nestedFiltered && !OnlyChangesDirectory(commands, k)) return false;

                repo     = nestedRepo;
                filtered = nestedFiltered;
                return true;
            }
        }

        return false;
    }

    /// <summary>A filtered create's run succeeds whatever gh did, so nothing before it may print a URL that would then
    /// read as its own: only <c>cd</c> may come first.</summary>
    static bool OnlyChangesDirectory(List<ShellCommand> commands, int k) =>
        commands.Take(k).All(c => c is { Scripts.Count: 0, RedirectsStdout: false, Words: ["cd", ..] });

    /// <summary><c>| tail -5</c>, <c>| head -n 20</c>: a filter reading only its input and keeping at least two lines,
    /// so what it prints is the create's own output with gh's "already exists:" line, the one before the URL, in view.
    /// </summary>
    static bool IsLineFilter(ShellCommand command) {
        if (command is not { Joiner: "|", Scripts.Count: 0, RedirectsStdout: false, ReadsElsewhere: false, Words: ["tail" or "head", .. var args] }) return false;

        var lines = 10;

        for (var i = 0; i < args.Count; i++) {
            var arg = args[i];
            var count = arg switch {
                "-n" when i + 1 < args.Count => args[++i],
                _ when arg.StartsWith("--lines=", StringComparison.Ordinal) => arg["--lines=".Length..],
                _ when arg.StartsWith("-n", StringComparison.Ordinal) => arg[2..],
                _ when arg.StartsWith('-') => arg[1..],
                _ => null,
            };

            if (count is not { Length: > 0 } || !count.All(char.IsAsciiDigit) || !int.TryParse(count, out lines)) return false;
        }

        return lines >= 2;
    }

    /// <summary><c>NAME=$(…) &amp;&amp; echo "$NAME"</c>: the echo prints the substitution's output and nothing else, and
    /// only once it succeeded. After <c>||</c>, <c>;</c> or a newline, or echoing anything else, it can print a URL the
    /// create never did.</summary>
    static bool EchoesAssignment(ShellCommand assignment, ShellCommand echo) {
        if (AssignedVariable(assignment) is not { } name) return false;

        return echo is { Joiner: "&&", Scripts.Count: 0, RedirectsStdout: false, Words: ["echo", var value] } && (value == "$" + name || value == "${" + name + "}");
    }

    /// <summary>False when an <c>||</c> since the last <c>;</c> or newline may skip it: after
    /// <c>gh pr view … || gh pr create</c>, a successful run printing a URL is the view's. A <c>;</c> inside a group
    /// settles nothing about whether the group runs, so the walk climbs out of each group that holds the command and
    /// passes over groups that closed before it.</summary>
    static bool RunsUnconditionally(List<ShellCommand> commands, int k) {
        var depths = new int[k + 1];
        var depth  = 0;

        for (var j = 0; j <= k; j++) {
            var joiner = commands[j].Joiner;
            depth += joiner.Count(c => c == '(') - joiner.Count(c => c == ')');
            if (commands[j].Words is ["{", ..]) depth++;
            if (commands[j].Words is ["}", ..]) depth--;
            depths[j] = depth;
        }

        var level = depths[k];

        for (var j = k; j > 0; j--) {
            var boundary = Math.Min(depths[j - 1], depths[j]);
            if (boundary > level) continue;

            level = boundary;
            var joiner = commands[j].Joiner;

            if (joiner.Contains("||", StringComparison.Ordinal)) return false;
            if (joiner.AsSpan().IndexOfAny(';', '\n') < 0) continue;
            if (level == 0) return true;

            level--;
        }

        return true;
    }

    /// <summary>The variable of a bare <c>NAME=$(…)</c>, whose exit status is the substitution's. Any other command
    /// holding one, such as <c>echo "$(gh pr create)"</c>, succeeds however the create fared.</summary>
    static string? AssignedVariable(ShellCommand command) =>
        command.Words is [var target] && target.EndsWith('=') && Assignment().IsMatch(target) ? target[..^1] : null;

    static bool Inspect(ShellCommand command, out string? script, out string? repo) {
        var words = command.Words;
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

        if (exe != "gh" || words.Count < i + 3 || words[i + 1] != "pr" || words[i + 2] != "create" || command.RedirectsStdout) return false;

        for (var j = i + 3; j < words.Count; j++) {
            var arg = words[j];

            if (arg == "--dry-run") return false;

            if (arg is "--repo" or "-R") {
                if (j + 1 < words.Count) repo = words[++j];
            } else if (arg.StartsWith("--repo=", StringComparison.Ordinal)) {
                repo = arg["--repo=".Length..];
            } else if (arg.StartsWith("-R", StringComparison.Ordinal) && arg.Length > 2) {
                repo = arg[2..];
            } else if (ValueFlags.Contains(arg)) {
                j++;
            }
        }

        return true;
    }

    /// <summary>Splits shell code into simple commands of words, from <paramref name="i"/>. A command substitution's
    /// body is opaque word text here, kept as one of its command's scripts, since it runs too. In a substitution
    /// it stops past the closing paren and says whether it found one.</summary>
    static (int End, bool Closed) Scan(string text, int i, bool substitution, List<ShellCommand> commands) {
        var current        = new ShellCommand("");
        var word           = new StringBuilder();
        var inWord         = false;
        var parens         = 0;
        var redirect       = false;
        var stdoutRedirect = false;
        var redirectTarget = false;
        var literal        = true;
        var heredocs       = new Queue<(string Word, bool StripTabs)>();

        while (i < text.Length) {
            var c = text[i];

            switch (c) {
                case ' ' or '\t' or '\r':
                    EndWord();
                    i++;
                    break;
                case '\n':
                    EndCommand("\n");
                    i = SkipHeredocBodies(text, i + 1, heredocs);
                    break;
                case ';' or '|' or '&': {
                    var doubled = c != ';' && i + 1 < text.Length && text[i + 1] == c;
                    EndCommand(doubled ? new string(c, 2) : c.ToString());
                    i += doubled ? 2 : 1;
                    break;
                }
                case '(':
                    EndCommand("(");
                    parens++;
                    i++;
                    break;
                case ')':
                    EndCommand(")");
                    i++;
                    if (substitution && parens-- == 0) return (i, true);
                    break;
                case '#' when !inWord:
                    while (i < text.Length && text[i] != '\n') i++;
                    break;
                case '\\':
                    if (i + 1 < text.Length && text[i + 1] != '\n') word.Append(text[i + 1]);
                    inWord  =  true;
                    literal =  false;
                    i      += 2;
                    break;
                case '\'': {
                    var end = text.IndexOf('\'', i + 1);
                    if (end < 0) end = text.Length;
                    word.Append(text, i + 1, end - i - 1);
                    inWord  = true;
                    literal = false;
                    i      = end + 1;
                    break;
                }
                case '"':
                    i      = ReadDoubleQuoted(text, i + 1, word, current.Scripts);
                    inWord  = true;
                    literal = false;
                    break;
                case '$' when i + 1 < text.Length && text[i + 1] == '(':
                    i      = ReadSubstitution(text, i + 2, current.Scripts);
                    inWord  = true;
                    literal = false;
                    break;
                case '$' when i + 1 < text.Length && text[i + 1] == '{': {
                    var end = text.IndexOf('}', i + 2);
                    end = end < 0 ? text.Length : end + 1;
                    word.Append(text, i, end - i);
                    inWord  = true;
                    literal = false;
                    i      = end;
                    break;
                }
                case '`':
                    i      = ReadBackticks(text, i + 1, current.Scripts);
                    inWord  = true;
                    literal = false;
                    break;
                case '<' when Heredoc().Match(text, i) is { Success: true, Index: var at } heredoc && at == i:
                    EndWord();
                    heredocs.Enqueue((Unquote(heredoc.Groups["word"].Value), heredoc.Groups["dash"].Success));
                    current.ReadsElsewhere = true;
                    i += heredoc.Length;
                    break;
                case '>' or '<':
                    // A redirection needs no space before it: in --title=New>/dev/null the argument ends at the '>'.
                    // Only bare digits before it name a file descriptor; a quoted "2" is an argument.
                    if (inWord && (!literal || !word.ToString().All(char.IsAsciiDigit))) EndWord();
                    redirect = true;
                    if (c == '>' && word is [] or ['1']) stdoutRedirect = true;
                    word.Append(c);
                    inWord = true;
                    i++;
                    while (i < text.Length && (text[i] is '&' || text[i] == c)) word.Append(text[i++]);
                    break;
                default:
                    word.Append(c);
                    inWord = true;
                    i++;
                    break;
            }
        }

        EndCommand("");
        return (i, false);

        void EndWord() {
            if (!inWord) return;

            // A redirection, and its target when a space parts them, is no argument: in --repo 2>/dev/null o/r
            // the repo is o/r.
            if (redirect) {
                current.RedirectsStdout |= stdoutRedirect;
                current.ReadsElsewhere  |= word.ToString().Contains('<');
                current.DiscardsStderr  |= word.ToString() is var text && text.StartsWith("2>", StringComparison.Ordinal) && text != "2>&1";
                redirectTarget          =  word[^1] is '>' or '<' or '&';
            } else if (redirectTarget) {
                redirectTarget = false;
            } else {
                current.Words.Add(word.ToString());
            }

            word.Clear();
            inWord         = false;
            redirect       = false;
            stdoutRedirect = false;
            literal        = true;
        }

        void EndCommand(string joiner) {
            EndWord();

            // A redirection alone stays a node: after (gh pr create) >/dev/null it is what the group's output meets.
            if (current.Words.Count > 0 || current.Scripts.Count > 0 || current.RedirectsStdout) {
                commands.Add(current);
                current = new ShellCommand(joiner);
            } else {
                current.RedirectsStdout = false;
                current.ReadsElsewhere  = false;
                current.DiscardsStderr  = false;
                if (joiner != "\n" || !current.Joiner.EndsWith('&') && !current.Joiner.EndsWith('|')) current.Joiner += joiner;
            }

            redirectTarget = false;
        }
    }

    /// <summary>Skips the bodies of the heredocs a line opened, up to and including each one's delimiter line.</summary>
    static int SkipHeredocBodies(string text, int i, Queue<(string Word, bool StripTabs)> heredocs) {
        while (heredocs.Count > 0 && i < text.Length) {
            var end  = text.IndexOf('\n', i);
            var line = text[i..(end < 0 ? text.Length : end)].TrimEnd('\r');
            var (word, stripTabs) = heredocs.Peek();

            if ((stripTabs ? line.TrimStart('\t') : line) == word) heredocs.Dequeue();

            i = end < 0 ? text.Length : end + 1;
        }

        return i;
    }

    static string Unquote(string word) => word.Replace("'", "").Replace("\"", "").Replace("\\", "");

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
        var (end, closed) = Scan(text, start, substitution: true, []);
        scripts.Add(text[start..(closed ? end - 1 : end)]);
        return end;
    }

    /// <param name="joiner">The operators between it and the command before, in order: <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>,
    /// a newline, a paren.</param>
    sealed class ShellCommand(string joiner) {
        public List<string> Words   { get; } = [];
        public List<string> Scripts { get; } = [];
        public string       Joiner  { get; set; } = joiner;

        /// <summary>Whether it sends stdout elsewhere (<c>&gt;file</c>, <c>1&gt;&gt;file</c>), so a URL printed earlier
        /// would read as the create's. Only unquoted syntax counts; <c>2&gt;&amp;1</c> leaves stdout alone.</summary>
        public bool RedirectsStdout { get; set; }

        /// <summary>Whether its stdin comes from a file or heredoc rather than the pipe before it.</summary>
        public bool ReadsElsewhere { get; set; }

        /// <summary>Whether it sends stderr anywhere but stdout, where gh reports a refused create.</summary>
        public bool DiscardsStderr { get; set; }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex Assignment();

    [GeneratedRegex("""\G<<(?<dash>-)?[ \t]*(?<word>'[^'\n]*'|"[^"\n]*"|\\?[A-Za-z0-9_.-]+)""")]
    private static partial Regex Heredoc();
}
