#!/usr/bin/env bash
# Fails on a banned comment token (CLAUDE.md, Comments) in a tracked source, build, shell or workflow file.
# Exit: 0 clean, 1 = violation found, 2 = checker failure (never "clean").
set -u -o pipefail
export LC_ALL=C

root="$(git rev-parse --show-toplevel)" || exit 2
cd "$root" || exit 2

# Plain POSIX awk: mawk on the Linux runners and BSD awk on macOS have no \b and no gensub.
# A comment opener counts only outside a quoted string on its line, and only after line start,
# whitespace or (C#) `;{})`: a raw or verbatim string spanning lines has no quote on its inner
# lines, and the guard keeps a URL or glob there from reading as a comment.
# A review bot's name alone is a domain term here (the desktop app renders Qodo's HTML); it is a
# review artifact only with an id, finding or fix after it.
read -r -d '' scan <<'AWK'
BEGIN {
    B = "(^|[^A-Za-z0-9_])"; E = "($|[^A-Za-z0-9_])"
    re = "§[ \t]?[0-9]"
    re = re "|" B "(Task|Finding|Decision) [0-9]+" E
    re = re "|" B "Phase [A-D0-9]($|[^A-Za-z_])"
    re = re "|" B "(Qodo|Bugbot|CodeRabbit)( ?#| ?[(]| PR| P[0-9]| review on| finding| fix| bug| regression|-flagged| flagged)"
    re = re "|" B "[Rr]ound-?[0-9]+ (review|fix|finding)"
    re = re "|" B "[Pp]re-fix" E
    re = re "|" B "[Aa]n earlier (revision|version|draft)" E
    re = re "|" B "BLOCKER-?[0-9]"
}
# Sets OPOS, OLEN and OBLOCK for the first comment opener outside a quoted string; OPOS 0 if none.
# C# has no single-quoted strings, only char literals, so `'"'` must not open one; shell `$'…'`
# takes backslash escapes where plain `'…'` does not.
function find_open(line, cs,    i, n, c, q, esc, p, prev) {
    OPOS = 0; q = ""; n = length(line)
    for (i = 1; i <= n; i++) {
        c = substr(line, i, 1)
        if (q != "") {
            if (c == "\\" && esc) i++
            else if (c == q) q = ""
            continue
        }
        if (c == "\"" || (c == "'" && !cs)) {
            q = c; esc = c == "\"" || substr(line, i - 1, 1) == "$"
            continue
        }
        if (c == "'") {
            if (substr(line, i + 1, 1) == "\\") { p = index(substr(line, i + 3), "'"); if (p) i += p + 2 }
            else if (substr(line, i + 2, 1) == "'") i += 2
            continue
        }
        prev = i == 1 ? " " : substr(line, i - 1, 1)
        if (cs) {
            if (prev !~ /[ \t;{})]/) continue
            if (substr(line, i, 2) == "//") { OPOS = i; OLEN = 2; OBLOCK = 0; return }
            if (substr(line, i, 2) == "/*") { OPOS = i; OLEN = 2; OBLOCK = 1; return }
        } else if (c == "#" && prev ~ /[ \t]/) { OPOS = i; OLEN = 1; OBLOCK = 0; return }
    }
}
function extract(line,    out, j) {
    out = ""
    while (line != "") {
        if (inblock) {
            j = index(line, closer)
            if (j == 0) return out " " line
            out = out " " substr(line, 1, j - 1)
            line = substr(line, j + length(closer)); inblock = 0
            continue
        }
        if (lang == "xml") { OPOS = index(line, "<!--"); OLEN = 4; OBLOCK = 1 }
        else find_open(line, lang == "cs")
        if (OPOS == 0) return out
        if (!OBLOCK) return out " " substr(line, OPOS + OLEN)
        line = substr(line, OPOS + OLEN); inblock = 1
    }
    return out
}
FNR == 1 {
    ext = FILENAME; sub(/.*\./, "", ext); inblock = 0
    lang = ext == "cs" ? "cs" : (ext == "sh" || ext == "yml" || ext == "yaml") ? "hash" : "xml"
    closer = lang == "cs" ? "*/" : "-->"
}
{
    text = extract($0)
    # An external standard's section (RFC 8628 §3.4, SemVer §10) is a citation, not a spec coordinate.
    gsub(/(RFC[ \t]+[0-9]+|SemVer)[ \t,]*§/, "", text)
    if (text !~ re) next
    line = $0
    sub(/^[ \t]+/, "", line)
    printf "%s\t%d\t%s\n", FILENAME, FNR, line
}
AWK

hits="$(git ls-files -z -- '*.cs' '*.axaml' '*.csproj' '*.props' '*.targets' '*.sh' '*.yml' '*.yaml' \
    | xargs -0 awk "$scan")"
rc=$?
[ "$rc" -ne 0 ] && { echo "::error::scanning tracked files failed (exit $rc) — treating as failure." >&2; exit 2; }
[ -z "$hits" ] && exit 0

while IFS=$'\t' read -r path line text; do
    echo "$path:$line: $text"
    echo "::error file=$path,line=$line::Banned comment token. State the constraint without the spec coordinate, review artifact or change narration (CLAUDE.md, Comments)."
done <<< "$hits"
exit 1
