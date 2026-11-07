using System.Globalization;
using Capacitor.Cli.Core.Install;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>
/// The bash script one administrator prompt runs as root. It writes its pid to the status file, replaces each link
/// still holding its original target, reports <see cref="Swapped"/> and waits for one word on the control pipe. On
/// <see cref="Go"/> it sets every entry aside and checks each against its expected kind and its own marker, renaming each
/// that passes to <see cref="AsideRetirement.CheckedAside"/>, so a failed check puts the whole install back. An aside an
/// earlier run left is deleted if it carries the checked name and checked like a fresh one otherwise. With the whole
/// install set aside it looks for what this user's own checks cannot see: a process of any account mapping a file of
/// it, or a launch agent in any home naming it, either of which puts it all back and reports <see cref="InUse"/>. Only
/// then does it delete anything. Any other word, end of file or no word in time puts the links back. After go it finishes even
/// if the app is gone.
/// <para>Every path reaches it as a positional argument, never as script text. It creates the status file itself, only
/// inside a real 0700 folder the app's user owns, and writes only through that descriptor: root must not write through
/// a path someone else controls. It holds that folder as its working directory and opens both channel files by name
/// inside it, so a rename or a link put at the folder's path after the check redirects neither.</para>
/// </summary>
internal static class PrivilegedSwapScript {
    internal const string Swapped  = "swapped";
    internal const string Restored = "restored";
    internal const string Deleted  = "deleted";
    internal const string Go       = "go";
    internal const string PutBack  = "put-back";
    internal const string InUse    = "in-use";

    internal static string Render(PrivilegedSpec spec) {
        Validate(spec);
        var wait = Math.Max(1, (int)Math.Ceiling(spec.GoWait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return $$"""
            #!/bin/bash
            set -u
            trap '' HUP PIPE
            umask 022

            status=$1 control=$2 owner=$3 stage=$4 homes=$5
            shift 5
            nlinks=$1; shift; k=0
            while [ $k -lt $nlinks ]; do links[$k]=$1; olds[$k]=$2; news[$k]=$3; shift 3; k=$((k + 1)); done
            nentries=$1; shift; k=0; m=0
            while [ $k -lt $nentries ]; do
                entries[$k]=$1; asides[$k]=$2; kinds[$k]=$3; markers[$k]=$4; daemons[$k]=$5; identities[$k]=$6; targets[$k]=$7
                first[$k]=$m; c=$8; shift 8
                while [ $c -gt 0 ]; do needs[$m]=$1; shift; m=$((m + 1)); c=$((c - 1)); done
                last[$k]=$m
                k=$((k + 1))
            done

            exists() { [ -e "$1" ] || [ -L "$1" ]; }
            kind() { if [ -L "$1" ]; then echo l; elif [ -d "$1" ]; then echo d; elif [ -e "$1" ]; then echo f; else echo -; fi; }

            if exists "$status"; then echo "kcap: $status is in the way" >&2; exit 2; fi
            dir=${status%/*}
            # A link put at the folder's path between these checks is followed, into a folder the check of . refuses.
            [ -d "$dir" ] && ! [ -L "$dir" ] && cd -P -- "$dir" && [ "$(/usr/bin/stat -f '%u %Lp' .)" = "$owner 700" ] \
                || { echo "kcap: $dir is not a private folder of user $owner" >&2; exit 2; }
            set -C
            exec 5>"${status##*/}" || exit 2
            set +C
            [ -f /dev/fd/5 ] || exit 2
            say() { echo "$1" >&5; }
            say $$

            # Read-only, so nothing is ever created through a link put at its name. The app holds the write end open, so
            # this returns at once; the watchdog ends a helper whose app died first.
            ( sleep {{wait}}; kill -TERM $$ ) </dev/null >/dev/null 2>&1 & watchdog=$!
            [ -p "${control##*/}" ] && ! [ -L "${control##*/}" ] && exec 3<"${control##*/}" && [ -p /dev/fd/3 ]; opened=$?
            kill $watchdog 2>/dev/null
            [ $opened = 0 ] || { echo "kcap: $control is not the control pipe" >&2; say {{Restored}}; exit 3; }

            relink() {
                [ -L "$1" ] && [ "$(/usr/bin/readlink -- "$1")" = "$2" ] || return 1
                # Without -h, ln follows a link someone planted at the staging name and makes kcap in its directory.
                local staged="$1.kcap-swap-$stage"
                /bin/ln -sh -- "$3" "$staged" || return 1
                [ -L "$staged" ] && [ "$(/usr/bin/readlink -- "$staged")" = "$3" ] && /bin/mv -fh -- "$staged" "$1" && return 0
                [ -L "$staged" ] && /bin/rm -f -- "$staged"
                return 1
            }

            put_back() {
                local i=$nlinks
                while [ $i -gt 0 ]; do
                    i=$((i - 1))
                    [ "${swapped[$i]:-}" = 1 ] || continue
                    relink "${links[$i]}" "${news[$i]}" "${olds[$i]}" || echo "kcap: left ${links[$i]}, which changed" >&2
                done
            }

            i=0
            while [ $i -lt $nlinks ]; do
                if ! relink "${links[$i]}" "${olds[$i]}" "${news[$i]}"; then
                    echo "kcap: ${links[$i]} is not the link that was planned" >&2
                    put_back; say {{Restored}}; exit 4
                fi
                swapped[$i]=1
                i=$((i + 1))
            done
            say {{Swapped}}

            word=
            IFS= read -r -t {{wait}} word <&3 || word=
            exec 3<&-
            if [ "$word" != {{Go}} ]; then put_back; say {{Restored}}; exit 0; fi

            # A process is found by the files it maps, which lsof names with every link resolved, whatever path it
            # was started by. A list without this helper's own process was not read. Asked once the install is set
            # aside, when no process can reach it by its own paths any more.
            in_use() {
                local i d e plist
                local -a look
                [ $nentries -gt 0 ] || return 1
                i=0
                while [ $i -lt $nentries ]; do
                    for e in "${entries[$i]}" "${asides[$i]}" "${asides[$i]}.ok"; do
                        look[${#look[@]}]=$e
                        d=$(cd -P -- "${e%/*}" 2>/dev/null && pwd -P) && look[${#look[@]}]=$d/${e##*/}
                    done
                    i=$((i + 1))
                done
                /usr/sbin/lsof -nPw -b -d txt -Fpn 2>/dev/null | /usr/bin/awk -v self="p$$" '
                    BEGIN { for (i = 1; i < ARGC; i++) look[i] = ARGV[i]; n = ARGC - 1; ARGC = 1 }
                    $0 == self { seen = 1 }
                    /^p/ { pid = substr($0, 2); next }
                    /^n/ {
                        f = substr($0, 2)
                        for (i = 1; i <= n; i++) if (f == look[i] || index(f, look[i] "/") == 1) { print "process " pid " runs " f; found = 1; exit }
                    }
                    END { exit (found ? 0 : (seen ? 1 : 2)) }' "${look[@]}"
                case $? in
                    0) return 0 ;;
                    1) ;;
                    *) echo "the process list could not be read"; return 0 ;;
                esac
                for plist in "$homes"/*/Library/LaunchAgents/io.kurrent.kcap.daemon.*.plist; do
                    [ -f "$plist" ] || continue
                    for e in "${look[@]}"; do
                        if /usr/bin/grep -qF -e "$e" -- "$plist"; then echo "$plist names $e"; return 0; fi
                    done
                done
                return 1
            }

            move_back() {
                local i=$nentries back=0 from
                while [ $i -gt 0 ]; do
                    i=$((i - 1))
                    case "${moved[$i]:-}" in
                        1) from=${asides[$i]} ;;
                        2) from=${asides[$i]}.ok ;;
                        *) continue ;;
                    esac
                    if exists "${entries[$i]}" || ! /bin/mv -h -- "$from" "${entries[$i]}"; then
                        echo "kcap: left ${entries[$i]} at $from" >&2
                        back=1
                    fi
                done
                put_back
                return $back
            }

            remove() {
                if [ -L "$1" ]; then /bin/rm -f -- "$1"
                elif [ -e "$1" ]; then /bin/rm -rf -- "$1"
                fi
                ! exists "$1"
            }

            ours() {
                [ "$(kind "$1")" = "${kinds[$2]}" ] || return 1
                if [ -n "${targets[$2]}" ]; then
                    local target
                    target=$(/usr/bin/readlink -- "$1") || return 1
                    target=${target%/}
                    [ "${target##*/}" = "${targets[$2]}" ] || return 1
                fi
                if [ -n "${daemons[$2]}" ]; then
                    [ "$(/usr/bin/stat -f '%d:%i:%m:%c' -- "$1/${daemons[$2]}" 2>/dev/null)" = "${identities[$2]}" ] || return 1
                fi
                [ -n "${markers[$2]}" ] || return 0
                [ "${kinds[$2]}" = d ] && [ -f "$1/${markers[$2]}" ] && ! [ -L "$1/${markers[$2]}" ] || return 1
                local j=${first[$2]}
                while [ $j -lt ${last[$2]} ]; do
                    /usr/bin/grep -qF -e "${needs[$j]}" -- "$1/${markers[$2]}" || return 1
                    j=$((j + 1))
                done
            }

            i=0
            while [ $i -lt $nentries ]; do
                checked="${asides[$i]}.ok"
                if ! remove "$checked"; then
                    echo "kcap: could not delete $checked" >&2
                    move_back && say {{Restored}}; exit 5
                fi
                if exists "${asides[$i]}"; then
                    if exists "${entries[$i]}"; then
                        echo "kcap: ${asides[$i]} is in the way of ${entries[$i]}" >&2
                        move_back && say {{Restored}}; exit 5
                    fi
                    moved[$i]=1
                elif exists "${entries[$i]}"; then
                    if ! /bin/mv -h -- "${entries[$i]}" "${asides[$i]}"; then
                        echo "kcap: could not set ${entries[$i]} aside" >&2
                        move_back && say {{Restored}}; exit 5
                    fi
                    moved[$i]=1
                fi
                if [ "${moved[$i]:-}" = 1 ]; then
                    if ! ours "${asides[$i]}" $i; then
                        echo "kcap: ${entries[$i]} is no longer the install that was recorded" >&2
                        move_back && say {{Restored}}; exit 5
                    fi
                    if exists "$checked" || ! /bin/mv -h -- "${asides[$i]}" "$checked"; then
                        echo "kcap: could not mark ${asides[$i]} checked" >&2
                        move_back && say {{Restored}}; exit 5
                    fi
                    moved[$i]=2
                fi
                i=$((i + 1))
            done

            if why=$(in_use); then
                echo "kcap: $why" >&2
                move_back && { say {{InUse}}; exit 0; }
                exit 7
            fi

            left=0
            i=0
            while [ $i -lt $nentries ]; do
                [ "${moved[$i]:-}" = 2 ] && ! remove "${asides[$i]}.ok" && left=1
                i=$((i + 1))
            done
            [ $left = 0 ] || { echo "kcap: part of the install could not be deleted" >&2; exit 6; }
            say {{Deleted}}

            """;
    }

    /// <summary>The script's positional arguments: the status file, the control pipe, the uid that must own their
    /// folder, the staging name's random part, the folder of every account's home, then the links as a count and
    /// (path, original, replacement) triples, and the entries as a count and (original, aside, kind d/l/f, marker,
    /// daemon path, daemon identity, link target name, substring count, substrings…) groups, each empty check
    /// skipped.</summary>
    internal static IReadOnlyList<string> Arguments(PrivilegedSpec spec, uint owner) {
        Validate(spec);
        var args = new List<string> { spec.StatusFile, spec.ControlFifo, Number(owner), spec.Staging, spec.Homes, Number(spec.Links.Count) };
        foreach (var link in spec.Links) args.AddRange([link.Path, link.Original, link.Replacement]);
        args.Add(Number(spec.Entries.Count));
        foreach (var entry in spec.Entries) {
            args.AddRange([
                entry.Entry.Original, entry.Entry.Aside, Letter(entry.Kind), entry.Marker ?? "",
                entry.Daemon?.Path ?? "", entry.Daemon?.Identity ?? "", entry.LinkTargetName ?? "", Number(entry.MarkerContains.Count),
            ]);
            args.AddRange(entry.MarkerContains);
        }
        return args;
    }

    static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);

    static string Letter(PrivilegedEntryKind kind) => kind switch {
        PrivilegedEntryKind.Directory => "d",
        PrivilegedEntryKind.Link      => "l",
        PrivilegedEntryKind.File      => "f",
        _                             => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// The kind each script install entry has; nothing else may reach root without a marker.
    static PrivilegedEntryKind? ScriptLayoutKind(string entry) => Path.GetFileName(entry) switch {
        ScriptInstallLayout.VersionsName   => PrivilegedEntryKind.Directory,
        ScriptInstallLayout.CurrentName    => PrivilegedEntryKind.Link,
        ScriptInstallLayout.MarkerFileName => PrivilegedEntryKind.File,
        _                                  => null,
    };

    // A line break would let `$(readlink)` match a target it trimmed and split a marker substring into two patterns.
    static void Validate(PrivilegedSpec spec) {
        if (spec.GoWait <= TimeSpan.Zero) throw new ArgumentException("The wait for go must be positive.", nameof(spec));

        // An unnormalised path, such as <link>//status, would let the script's folder check see past a link.
        Require(spec.ControlFifo, Normal, "control pipe");
        Require(spec.StatusFile, Normal, "status file");
        Require(spec.Homes, Normal, "homes folder");
        Require(spec.Staging, s => s.Length >= 32 && s.All(char.IsAsciiHexDigitLower), "staging name");
        if (Path.GetDirectoryName(spec.StatusFile) != Path.GetDirectoryName(spec.ControlFifo))
            throw new ArgumentException("The status file and the control pipe must share one folder.", nameof(spec));
        foreach (var link in spec.Links) {
            Require(link.Path, Path.IsPathFullyQualified, "link");
            Require(link.Original, _ => true, "link target");
            Require(link.Replacement, Path.IsPathFullyQualified, "link replacement");
        }
        foreach (var (entry, kind, marker, contains, daemon, linkTarget) in spec.Entries) {
            Require(entry.Original, Normal, "entry");
            if (daemon is var (daemonPath, identity)) {
                if (kind != PrivilegedEntryKind.Directory) throw new ArgumentException($"Only a directory holds the daemon, not {entry.Original}.", nameof(spec));
                Require(daemonPath, p => !Path.IsPathRooted(p) && !p.Split('/').Contains(".."), "daemon path");
                Require(identity, i => i.Split(':') is { Length: 4 } parts && parts.All(n => n.Length > 0 && n.All(char.IsAsciiDigit)), "daemon identity");
            }
            if (linkTarget is not null) {
                if (kind != PrivilegedEntryKind.Link) throw new ArgumentException($"Only a link has a target, not {entry.Original}.", nameof(spec));
                Require(linkTarget, t => !t.Contains('/') && t is not ("." or ".."), "link target name");
            }
            Require(entry.Aside, a => AsideRetirement.IsOwnAside(entry.Original, a, linkAllowed: true), "aside");
            if (string.IsNullOrEmpty(marker)) {
                if (contains.Count > 0 || ScriptLayoutKind(entry.Original) != kind)
                    throw new ArgumentException($"{entry.Original} needs a marker to be deleted as a {kind}.", nameof(spec));
            } else {
                Require(marker, m => !Path.IsPathRooted(m) && !m.Split('/').Contains(".."), "marker");
                if (kind != PrivilegedEntryKind.Directory || contains.Count == 0)
                    throw new ArgumentException($"The marker of {entry.Original} needs a directory and at least one substring.", nameof(spec));
            }
            foreach (var needed in contains) Require(needed, _ => true, "marker substring");
        }
    }

    static bool Normal(string path) => Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path;

    static void Require(string value, Func<string, bool> valid, string what) {
        if (string.IsNullOrEmpty(value) || value.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0 || !valid(value))
            throw new ArgumentException($"The privileged helper cannot take {what} '{value}'.");
    }
}
