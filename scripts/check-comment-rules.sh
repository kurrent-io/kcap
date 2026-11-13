#!/usr/bin/env bash
# Fails on a banned comment token (CLAUDE.md, Comments) in a tracked source, build, shell or workflow file.
# Exit: 0 clean, 1 = violation found, 2 = checker failure (never "clean").
set -u -o pipefail
export LC_ALL=C

root="$(git rev-parse --show-toplevel)" || exit 2
cd "$root" || exit 2

# Plain POSIX awk: mawk on the Linux runners and BSD awk on macOS have no \b and no gensub.
# Each language gets a small lexer whose string and comment state carries across lines, so text
# inside any string form is never read as a comment and a comment is found wherever it starts.
# A review bot's name alone is a domain term here (the desktop app renders Qodo's HTML); it is a
# review artifact only with an id, finding or fix after it.
read -r -d '' scan <<'AWK'
BEGIN {
    B = "(^|[^A-Za-z0-9_])"; E = "($|[^A-Za-z0-9_])"
    re = "§[ \t]?[0-9]"
    re = re "|" B "(Task|Finding|Decision) [0-9]+" E
    re = re "|" B "Phase [A-D0-9]($|[^A-Za-z_])"
    re = re "|" B "(Qodo|Bugbot|CodeRabbit)( ?#| ?[(](#|PR|[0-9])| PR| P[0-9]| review on| finding| fix| bug| regression|-flagged| flagged)"
    re = re "|" B "[Rr]ound-?[0-9]+ (review|fix|finding)"
    re = re "|" B "[Pp]re-fix" E
    re = re "|" B "[Aa]n earlier (revision|version|draft)" E
    re = re "|" B "BLOCKER-?[0-9]"
}
function qrun(line, i,    k) {
    k = 0
    while (substr(line, i + k, 1) == "\"") k++
    return k
}
# C#: strings nest through interpolation holes, so they live on a stack. Regular strings end with
# their line; verbatim (@"", "" escapes) and raw ("""…""") strings may span lines. A `{` in an
# interpolated string opens a code hole (`{{` is literal; a raw string with N `$` needs N braces).
# A char literal such as '"' opens no string.
function cs_push(kind, n, dol) { sp++; sk[sp] = kind; sn[sp] = n; sd[sp] = dol; hb[sp] = 0 }
function cs_open(line, i,    j, dol, at, k) {
    j = i - 1; dol = 0; at = 0
    while (j >= 1 && substr(line, j, 1) ~ /[$@]/) { if (substr(line, j, 1) == "$") dol++; else at = 1; j-- }
    if (at) { cs_push("verb", 0, dol); return i + 1 }
    k = qrun(line, i)
    if (k >= 3) { cs_push("raw", k, dol); return i + k }
    if (k == 2) return i + 2
    cs_push("reg", 0, dol); return i + 1
}
function lex_cs(line,    out, i, n, c, j, k, t) {
    out = ""; n = length(line); i = 1
    while (i <= n) {
        c = substr(line, i, 1)
        if (cblock) {
            j = index(substr(line, i), "*/")
            if (j == 0) return out " " substr(line, i)
            out = out " " substr(line, i, j - 1); i += j + 1; cblock = 0; continue
        }
        t = sp ? sk[sp] : ""
        if (t == "reg" || t == "verb" || t == "raw") {
            if (t == "reg" && c == "\\") { i += 2; continue }
            if (c == "\"") {
                if (t == "raw") { k = qrun(line, i); if (k >= sn[sp]) sp--; i += k; continue }
                if (t == "verb" && substr(line, i + 1, 1) == "\"") { i += 2; continue }
                sp--; i++; continue
            }
            if (sd[sp] && c == "{") {
                k = 0; while (substr(line, i + k, 1) == "{") k++
                if (t == "raw") { if (k >= sd[sp]) cs_push("hole", 0, sd[sp]); i += k; continue }
                if (k >= 2) { i += 2; continue }
                cs_push("hole", 0, 1); i++; continue
            }
            if (sd[sp] && t != "raw" && c == "}" && substr(line, i + 1, 1) == "}") { i += 2; continue }
            i++; continue
        }
        if (sp == 0 && substr(line, i, 2) == "//") return out " " substr(line, i + 2)
        if (sp == 0 && substr(line, i, 2) == "/*") { cblock = 1; i += 2; continue }
        if (c == "'") {
            if (substr(line, i + 1, 1) == "\\") { j = index(substr(line, i + 3), "'"); i += j ? j + 3 : 1 }
            else if (substr(line, i + 2, 1) == "'") i += 3
            else i++
            continue
        }
        if (c == "\"") { i = cs_open(line, i); continue }
        if (t == "hole") {
            if (c == "{") hb[sp]++
            else if (c == "}") {
                if (hb[sp] == 0) { k = sd[sp]; sp--; i += sk[sp] == "raw" ? k : 1; continue }
                hb[sp]--
            }
        }
        i++
    }
    for (j = 1; j <= sp; j++) if (sk[j] == "reg") { sp = j - 1; break }
    return out
}
# Shell: quoting nests through `$(…)` and `${…}`, so it lives on a stack. '…' takes no escapes, "…"
# and $'…' do, and all of them may span lines. `#` opens a comment only at the start of a word. A
# heredoc body is data until its delimiter line.
function sh_push(kind) { shp++; hk[shp] = kind; hdep[shp] = 0 }
function lex_sh(line,    out, i, n, c, t, rest) {
    if (hd != "") {
        t = line
        if (hdstrip) sub(/^\t+/, "", t)
        if (t == hd) hd = ""
        return ""
    }
    out = ""; n = length(line); i = 1
    while (i <= n) {
        c = substr(line, i, 1); t = shp ? hk[shp] : ""
        if (t == "sq") { if (c == "'") shp--; i++; continue }
        if (t == "dq" || t == "ansi") {
            if (c == "\\") { i += 2; continue }
            if ((t == "dq" && c == "\"") || (t == "ansi" && c == "'")) { shp--; i++; continue }
            if (t == "dq" && substr(line, i, 2) == "$(") { sh_push("sub"); i += 2; continue }
            if (t == "dq" && substr(line, i, 2) == "${") { sh_push("brace"); i += 2; continue }
            i++; continue
        }
        if (c == "\\") { i += 2; continue }
        if (c == "'") { sh_push(substr(line, i - 1, 1) == "$" ? "ansi" : "sq"); i++; continue }
        if (c == "\"") { sh_push("dq"); i++; continue }
        if (substr(line, i, 2) == "$(") { sh_push("sub"); i += 2; continue }
        if (t == "sub" && c == "(") hdep[shp]++
        if (t == "sub" && c == ")") { if (hdep[shp] == 0) { shp--; i++; continue }; hdep[shp]-- }
        if (t == "brace" && c == "{") hdep[shp]++
        if (t == "brace" && c == "}") { if (hdep[shp] == 0) { shp--; i++; continue }; hdep[shp]-- }
        if (t != "brace" && c == "#" && (i == 1 || substr(line, i - 1, 1) ~ /[ \t;&|()<>]/)) { out = out " " substr(line, i + 1); break }
        if (shp == 0 && substr(line, i, 2) == "<<" && substr(line, i + 2, 1) != "<") {
            rest = substr(line, i + 2); pstrip = 0
            if (substr(rest, 1, 1) == "-") { pstrip = 1; rest = substr(rest, 2) }
            sub(/^[ \t]+/, "", rest)
            if (match(rest, /^'[A-Za-z_][A-Za-z0-9_]*'/) || match(rest, /^"[A-Za-z_][A-Za-z0-9_]*"/)) pend = substr(rest, 2, RLENGTH - 2)
            else if (match(rest, /^\\?[A-Za-z_][A-Za-z0-9_]*/)) { pend = substr(rest, 1, RLENGTH); sub(/^\\/, "", pend) }
            i += 2; continue
        }
        i++
    }
    if (pend != "") { hd = pend; hdstrip = pstrip; pend = "" }
    return out
}
# YAML: a quote opens a scalar only where a scalar starts ('' and \" are escapes), and `#` opens a
# comment after whitespace. A `run:` block scalar is shell; any other block scalar is literal text.
function scalar_start(line, i,    j, p) {
    j = i - 1
    while (j >= 1 && substr(line, j, 1) ~ /[ \t]/) j--
    if (j == 0) return 1
    p = substr(line, j, 1)
    return p ~ /[[{,]/ || (p ~ /[:?-]/ && j < i - 1)
}
function lex_yml(line,    out, i, n, c, ind) {
    if (yb) {
        if (line ~ /^[ \t]*$/) return ""
        match(line, /^ */); ind = RLENGTH
        if (ind > ybind) {
            if (!ycind) ycind = ind
            return ybshell ? lex_sh(substr(line, ycind + 1)) : ""
        }
        yb = 0; shp = 0; hd = ""; pend = ""
    }
    out = ""; n = length(line); i = 1
    while (i <= n) {
        c = substr(line, i, 1)
        if (yst == "sq") {
            if (c == "'") { if (substr(line, i + 1, 1) == "'") { i += 2; continue }; yst = "" }
            i++; continue
        }
        if (yst == "dq") { if (c == "\\") i++; else if (c == "\"") yst = ""; i++; continue }
        if (c == "#" && (i == 1 || substr(line, i - 1, 1) ~ /[ \t]/)) { out = out " " substr(line, i + 1); break }
        if ((c == "'" || c == "\"") && scalar_start(line, i)) { yst = c == "'" ? "sq" : "dq"; i++; continue }
        i++
    }
    if (yst == "" && line ~ /^[ \t]*(- )?[^#'"]*:[ \t]+[|>][-+0-9]*[ \t]*(#.*)?$/) {
        match(line, /^ */); ybind = RLENGTH; yb = 1; ycind = 0
        ybshell = line ~ /^[ \t]*(- )?run:/
        shp = 0; hd = ""; pend = ""
    }
    return out
}
function lex_xml(line,    out, j, k) {
    out = ""
    while (line != "") {
        if (st == "cdata") { j = index(line, "]]>"); if (j == 0) return out; line = substr(line, j + 3); st = ""; continue }
        if (st == "block") {
            j = index(line, "-->")
            if (j == 0) return out " " line
            out = out " " substr(line, 1, j - 1); line = substr(line, j + 3); st = ""; continue
        }
        j = index(line, "<!--"); k = index(line, "<![CDATA[")
        if (k && (!j || k < j)) { line = substr(line, k + 9); st = "cdata"; continue }
        if (!j) return out
        line = substr(line, j + 4); st = "block"
    }
    return out
}
FNR == 1 {
    ext = FILENAME; sub(/.*\./, "", ext)
    lang = ext == "cs" ? "cs" : ext == "sh" ? "sh" : (ext == "yml" || ext == "yaml") ? "yml" : "xml"
    st = ""; yst = ""; hd = ""; pend = ""; yb = 0; sp = 0; cblock = 0; shp = 0
}
{
    if (lang == "cs") text = lex_cs($0)
    else if (lang == "sh") text = lex_sh($0)
    else if (lang == "yml") text = lex_yml($0)
    else text = lex_xml($0)
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
