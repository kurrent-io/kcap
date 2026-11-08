namespace Capacitor.Cli.Core;

public static class GitPorcelain {
    /// <summary>The records of <c>git status --porcelain=v1 -z</c>, in git's order. A record cut short by
    /// a truncated read is dropped rather than returned with a partial path.</summary>
    public static IEnumerable<PorcelainEntry> ParseZ(string output) {
        var fields = output.Split('\0');
        // A complete output ends with a NUL, so its last field is empty; anything else is a cut-off record.
        var complete = fields.Length - 1;

        for (var i = 0; i < complete; i++) {
            if (fields[i].Length < 4) continue;

            var code = fields[i][..2];
            // A worktree-side rename (" R", a moved intent-to-add file) carries the original path too.
            if (code[0] is 'R' or 'C' || code[1] is 'R' or 'C') {
                if (i + 1 >= complete) yield break;

                yield return new PorcelainEntry(code, fields[i][3..], fields[i + 1]);
                i++;
                continue;
            }

            yield return new PorcelainEntry(code, fields[i][3..]);
        }
    }
}
