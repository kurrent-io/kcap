namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>The <c>statusLine.command</c> wiring writes. The account id travels in the command because
/// <c>CLAUDE_CODE_SUBPROCESS_ENV_SCRUB</c> can strip <c>CLAUDE_CONFIG_DIR</c> from the status line's environment.</summary>
public static class ClaudeStatusLineCommand {
    const string Plain = "kcap statusline --account ";
    const string Guard = "if command -v kcap >/dev/null 2>&1; then exec " + Plain;
    const string Else  = "; else ";
    const string End   = "; fi";

    /// <summary>With a POSIX shell, a missing kcap still runs the original. The original is one single-quoted
    /// argument to a nested <c>sh -c</c>, never spliced into the guard, so its comments, newlines and heredocs
    /// parse exactly as they did on their own. PowerShell gets the plain command, and a missing kcap blanks the line.</summary>
    public static string For(string accountId, string? original, bool posixShell) =>
        !posixShell ? Plain + accountId
                    : Guard + accountId + Else + (original is null ? ":" : "exec sh -c " + SingleQuoted(original)) + End;

    public static bool IsOurs(string? command) => AccountOf(command) is not null;

    internal static string? AccountOf(string? command) {
        if (command is null) return null;

        string id;
        if (command.StartsWith(Guard, StringComparison.Ordinal)) {
            var rest = command[Guard.Length..];
            var cut  = rest.IndexOf(Else, StringComparison.Ordinal);
            if (cut < 0 || !rest.EndsWith(End, StringComparison.Ordinal)) return null;
            id = rest[..cut];
        } else if (command.StartsWith(Plain, StringComparison.Ordinal)) {
            id = command[Plain.Length..];
        } else {
            return null;
        }

        return id.Length > 0 && !id.Any(char.IsWhiteSpace) ? id : null;
    }

    /// <summary>The original a POSIX guard carries, so a lost backup can be recovered from the settings file
    /// instead of discarding the user's command. False for the plain shape, which carries none.</summary>
    internal static bool TryOriginalOf(string command, out string? original) {
        original = null;
        if (AccountOf(command) is null || !command.StartsWith(Guard, StringComparison.Ordinal)) return false;

        var rest   = command[Guard.Length..];
        var branch = rest[(rest.IndexOf(Else, StringComparison.Ordinal) + Else.Length)..^End.Length];
        if (branch == ":") return true;

        const string run = "exec sh -c '";
        if (!branch.StartsWith(run, StringComparison.Ordinal) || branch.Length < run.Length + 1 || branch[^1] != '\'') return false;

        var quoted = branch[run.Length..^1];
        if (quoted.Replace("'\\''", "").Contains('\'')) return false;

        original = quoted.Replace("'\\''", "'");
        return true;
    }

    public static bool PosixShell(Func<string, string?> env) => PosixShellPath(env) is not null;

    public static string? PosixShellPath(Func<string, string?> env) => PosixShellPath(env, OperatingSystem.IsWindows(), File.Exists);

    static readonly string[] ShellNames = ["bash.exe", "sh.exe", "bash", "sh"];

    static readonly string[] StandardGitBash = [@"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe"];

    /// <summary>The POSIX shell Claude runs hooks and the status line with, null when it falls back to PowerShell.
    /// Claude's own lookup, in its order: the configured shell, the standard Git install folders, then bash beside
    /// the first <c>git.exe</c> on PATH only. Looking further than Claude does would claim a Bash it does not have.</summary>
    internal static string? PosixShellPath(Func<string, string?> env, bool windows, Func<string, bool> fileExists) {
        if (!windows) return "sh";

        if (env("CLAUDE_CODE_GIT_BASH_PATH") is { Length: > 0 } configured
         && ShellNames.Contains(Path.GetFileName(configured).ToLowerInvariant()) && fileExists(configured))
            return configured;

        foreach (var standard in StandardGitBash)
            if (fileExists(standard)) return standard;

        foreach (var dir in (env("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (!fileExists(Path.Combine(dir, "git.exe"))) continue;

            var bash = Path.GetFullPath(Path.Combine(dir, "..", "bin", "bash.exe"));
            return fileExists(bash) ? bash : null;
        }

        return null;
    }

    static string SingleQuoted(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
