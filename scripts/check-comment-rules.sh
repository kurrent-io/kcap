#!/usr/bin/env bash
# Fails on a banned comment token (CLAUDE.md, Comments) in a tracked source, build, shell or workflow file.
# Exit: 0 clean, 1 = violation found, 2 = checker failure (never "clean").
set -u
export LC_ALL=C

root="$(git rev-parse --show-toplevel)" || exit 2
cd "$root" || exit 2

# Plain POSIX awk: mawk on the Linux runners and BSD awk on macOS have no \b and no gensub.
# A `//`, `/*` or `#` opens a comment only at line start or after whitespace, so URLs and
# globs such as "src/**/*.cs" inside strings do not.
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
function extract(line, open_re, closer, line_re,    out, j, oi, ol, li, ll) {
    out = ""
    while (line != "") {
        if (inblock) {
            j = index(line, closer)
            if (j == 0) return out " " line
            out = out " " substr(line, 1, j - 1)
            line = substr(line, j + length(closer)); inblock = 0
            continue
        }
        oi = open_re == "" ? 0 : match(line, open_re); ol = RLENGTH
        li = line_re == "" ? 0 : match(line, line_re); ll = RLENGTH
        if (li > 0 && (oi == 0 || li < oi)) return out " " substr(line, li + ll)
        if (oi == 0) return out
        line = substr(line, oi + ol); inblock = 1
    }
    return out
}
FNR == 1 { ext = FILENAME; sub(/.*\./, "", ext); inblock = 0 }
{
    if (ext == "cs") text = extract($0, "(^|[ \t])/\\*", "*/", "(^|[ \t])//")
    else if (ext == "sh" || ext == "yml" || ext == "yaml") text = extract($0, "", "", "(^|[ \t])#")
    else text = extract($0, "<!--", "-->", "")
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
