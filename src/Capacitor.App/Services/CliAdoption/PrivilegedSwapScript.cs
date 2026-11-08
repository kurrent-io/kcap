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
/// it, or a launch agent or one of <see cref="PrivilegedSpec.Sites"/> in any home naming it, any of which puts it
/// all back and reports <see cref="InUse"/>. Only then does it delete anything. A kcap link in another account's home, or
/// a machine-wide one, that points into the install keeps it the same way, checked before anything is set aside and
/// again after. Any other word, end of file or no word in time puts the links back. After go it finishes even if the app
/// is gone.
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
            nfiles=$1; shift; k=0
            while [ $k -lt $nfiles ]; do files[$k]=$1; shift; k=$((k + 1)); done
            registry=$1; nkinds=$2; shift 2; k=0
            while [ $k -lt $nkinds ]; do accountfiles[$k]=$1; shift; k=$((k + 1)); done
            nhomelinks=$1; shift; k=0
            while [ $k -lt $nhomelinks ]; do homelinks[$k]=$1; shift; k=$((k + 1)); done
            nneedles=$1; shift; k=0
            while [ $k -lt $nneedles ]; do needles[$k]=$1; shift; k=$((k + 1)); done
            nlinks=$1; shift; k=0
            while [ $k -lt $nlinks ]; do links[$k]=$1; olds[$k]=$2; news[$k]=$3; shift 3; k=$((k + 1)); done
            nentries=$1; shift; k=0; m=0; n=0
            while [ $k -lt $nentries ]; do
                entries[$k]=$1; asides[$k]=$2; kinds[$k]=$3; markers[$k]=$4; pins[$k]=$5; identities[$k]=$6; targets[$k]=$7
                present[$k]=$8; first[$k]=$m; c=$9; shift 9
                while [ $c -gt 0 ]; do needs[$m]=$1; shift; m=$((m + 1)); c=$((c - 1)); done
                last[$k]=$m
                kids[$k]=$1; shift; kfirst[$k]=$n
                if [ "${kids[$k]}" != - ]; then
                    c=${kids[$k]}
                    while [ $c -gt 0 ]; do kid[$n]=$1; shift; n=$((n + 1)); c=$((c - 1)); done
                fi
                klast[$k]=$n
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

            # Each entry under each name it can have, as written and with its folder's links resolved.
            look=()
            i=0
            while [ $i -lt $nentries ]; do
                for e in "${entries[$i]}" "${asides[$i]}" "${asides[$i]}.ok"; do
                    look[${#look[@]}]=$e
                    d=$(cd -P -- "${e%/*}" 2>/dev/null && pwd -P) && look[${#look[@]}]=$d/${e##*/}
                done
                i=$((i + 1))
            done

            # Another account can put a pipe at any of these names, which root would wait on for good: only a regular file
            # is read, and grep skips one swapped in after that check. A file names the install as JSON or XML may store it,
            # and one grep cannot read may name it all the same.
            names() {
                local e k=0
                [ -f "$1" ] || return 1
                for e in "${look[@]}"; do
                    /usr/bin/grep -qF -D skip -e "$e" -- "$1" 2>/dev/null
                    [ $? = 1 ] || return 0
                done
                while [ $k -lt $nneedles ]; do
                    /usr/bin/grep -qF -D skip -e "${needles[$k]}" -- "$1" 2>/dev/null
                    [ $? = 1 ] || return 0
                    k=$((k + 1))
                done
                return 1
            }

            # Follows a link through every hop, as the kernel would, at most 32 deep. Succeeds when a hop, or where it ends
            # with its folders' links resolved, lies inside the install. The deepest of those folders still there resolves
            # the path, so a link into an install already set aside is still seen.
            leads_inside() {
                local p=$1 t d rest n=0
                while [ -L "$p" ] && [ $n -lt 32 ]; do
                    t=$(/usr/bin/readlink -- "$p") || return 1
                    case "$t" in /*) p=$t ;; *) p=${p%/*}/$t ;; esac
                    inside "$p" && return 0
                    n=$((n + 1))
                done
                d=${p%/*} rest=${p##*/}
                until t=$(cd -P -- "${d:-/}" 2>/dev/null && pwd -P); do
                    case "$d" in */*) ;; *) return 1 ;; esac
                    rest=${d##*/}/$rest d=${d%/*}
                done
                inside "${t%/}/$rest"
            }

            inside() {
                local e
                for e in "${look[@]}"; do case "$1" in "$e"|"$e"/*) return 0 ;; esac; done
                return 1
            }

            # Every folder in the homes folder and, for the machine's own, every account's home the directory service
            # knows, wherever it lives. No list from the directory service leaves accounts nothing else finds, so the
            # checks that read the homes keep the install. So does a user's home that is not there, as a network home
            # not mounted, which may name the install once it is; a system account's home often is not there at all.
            homelist=()
            for h in "$homes"/*; do [ -d "$h" ] && homelist[${#homelist[@]}]=$h; done
            homes_read=1 absent=
            if [ "$homes" = /Users ]; then
                if accounts=$(/usr/bin/dscl . -list /Users NFSHomeDirectory 2>/dev/null) && [ -n "$accounts" ] \
                    && ids=$(/usr/bin/dscl . -list /Users UniqueID 2>/dev/null) && [ -n "$ids" ]; then
                    nl=$'\n'
                    people=$nl$(printf '%s\n' "$ids" | /usr/bin/awk '$2 >= 500 { print $1 }')$nl
                    while read -r name h; do
                        [ -n "$h" ] || continue
                        homelist[${#homelist[@]}]=$h
                        case "$people" in *"$nl$name$nl"*) [ -d "$h" ] || absent=$h ;; esac
                    done <<< "$accounts"
                else
                    homes_read=0
                fi
            fi
            homes_of() {
                [ ${#homelist[@]} -gt 0 ] || return 0
                printf '%s\n' "${homelist[@]}" | /usr/bin/sort -u
            }

            # The folder of every account in a registry, one per line; a failure when the file is not one.
            jsfolders='ObjC.import("Foundation");
            function run(argv) {
                const text = $.NSString.stringWithContentsOfFileEncodingError(argv[0], $.NSUTF8StringEncoding, null);
                if (text.isNil()) throw new Error("unreadable");
                const accounts = JSON.parse(text.js).accounts ?? [];
                if (!Array.isArray(accounts)) throw new Error("no account list");
                const folders = accounts.map(a => a.directory);
                if (folders.some(f => typeof f !== "string" || !f.startsWith("/") || f.includes("\n"))) throw new Error("a folder cannot be read");
                return folders.join("\n");
            }'

            # A process is found by the files it maps, which lsof names with every link resolved, whatever path it
            # was started by. A list without this helper's own process was not read. Asked once the install is set
            # aside, when no process can reach it by its own paths any more and a link made since the first look shows.
            in_use() {
                local i d home plist listed st
                [ $nentries -gt 0 ] || return 1
                [ $homes_read = 1 ] || { echo "the accounts on this Mac could not be listed"; return 0; }
                linked && return 0
                /usr/sbin/lsof -nPw -b -d txt -Fpn 2>/dev/null | /usr/bin/awk -v self="p$$" '
                    BEGIN { for (i = 1; i < ARGC; i++) look[i] = ARGV[i]; n = ARGC - 1; ARGC = 1 }
                    $0 == self { seen = 1 }
                    /^p/ { pid = substr($0, 2); next }
                    /^n/ {
                        f = substr($0, 2)
                        for (i = 1; i <= n; i++) if (f == look[i] || index(f, look[i] "/") == 1) { print "process " pid " runs " f; found = 1; exit }
                    }
                    END { exit (found ? 0 : (seen ? 1 : 2)) }' "${look[@]}"
                st=("${PIPESTATUS[@]}")
                # lsof warns and goes on past an error, so a list with this helper in it may still leave a process out.
                case ${st[1]} in
                    0) return 0 ;;
                    1) [ "${st[0]}" = 0 ] || { echo "the process list could not be read"; return 0; } ;;
                    *) echo "the process list could not be read"; return 0 ;;
                esac
                while IFS= read -r home; do
                    [ -d "$home" ] || continue
                    # A folder that cannot be listed reads as empty, and may hold a launch agent naming the install.
                    for d in "$home" "$home/Library" "$home/Library/LaunchAgents"; do
                        if exists "$d" && ! /bin/ls -A -- "$d" >/dev/null 2>&1; then echo "$d cannot be listed"; return 0; fi
                    done
                    for plist in "$home"/Library/LaunchAgents/*.plist; do
                        if names "$plist"; then echo "$plist names the install"; return 0; fi
                    done
                    i=0
                    while [ $i -lt $nfiles ]; do
                        if names "$home/${files[$i]}"; then echo "$home/${files[$i]} names the install"; return 0; fi
                        i=$((i + 1))
                    done
                    [ -n "$registry" ] && { [ -e "$home/$registry" ] || [ -L "$home/$registry" ]; } || continue
                    # A registry that cannot be parsed may list any folder, so it keeps the install.
                    if ! [ -f "$home/$registry" ] || ! listed=$(/usr/bin/osascript -l JavaScript -e "$jsfolders" "$home/$registry" 2>/dev/null); then
                        echo "$home/$registry cannot be read"; return 0
                    fi
                    while IFS= read -r d; do
                        [ -n "$d" ] || continue
                        # One there that cannot be listed may hold files naming the install; one gone names nothing, as in
                        # this user's own check.
                        if exists "$d" && ! /bin/ls -A -- "$d" >/dev/null 2>&1; then echo "$d cannot be listed"; return 0; fi
                        i=0
                        while [ $i -lt $nkinds ]; do
                            if names "$d/${accountfiles[$i]}"; then echo "$d/${accountfiles[$i]} names the install"; return 0; fi
                            i=$((i + 1))
                        done
                    done <<< "$listed"
                done < <(homes_of)
                return 1
            }

            # Another account's own kcap link into the install would dangle once it goes; root changes nothing in another
            # account's home, so the install stays, and so it does for a machine-wide link the plan did not swap. Read
            # before the install is set aside and again after. An absolute link path is the machine's, read once.
            linked() {
                local home l i=0
                [ $nentries -gt 0 ] || return 1
                while [ $i -lt $nhomelinks ]; do
                    l=${homelinks[$i]}
                    i=$((i + 1))
                    case "$l" in /*) if leads_inside "$l"; then echo "$l points into the install"; return 0; fi ;; esac
                done
                [ $homes_read = 1 ] || { echo "the accounts on this Mac could not be listed"; return 0; }
                [ -z "$absent" ] || { echo "the home $absent is not there"; return 0; }
                while IFS= read -r home; do
                    [ -d "$home" ] || continue
                    i=0
                    while [ $i -lt $nhomelinks ]; do
                        l=${homelinks[$i]}
                        i=$((i + 1))
                        case "$l" in /*) continue ;; esac
                        if leads_inside "$home/$l"; then echo "$home/$l points into the install"; return 0; fi
                    done
                done < <(homes_of)
                return 1
            }

            if why=$(linked); then
                echo "kcap: $why" >&2
                put_back; say {{InUse}}; exit 0
            fi

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
                if [ "${kids[$2]}" != - ]; then
                    [ "$(/bin/ls -A -- "$1" | /usr/bin/wc -l | /usr/bin/tr -d ' ')" = "${kids[$2]}" ] || return 1
                    local x=${kfirst[$2]}
                    while [ $x -lt ${klast[$2]} ]; do exists "$1/${kid[$x]}" || return 1; x=$((x + 1)); done
                fi
                if [ -n "${targets[$2]}" ]; then
                    local target
                    target=$(/usr/bin/readlink -- "$1") || return 1
                    target=${target%/}
                    [ "${target##*/}" = "${targets[$2]}" ] || return 1
                fi
                if [ -n "${pins[$2]}" ]; then
                    [ "$(/usr/bin/stat -f '%d:%i:%m:%c' -- "$1/${pins[$2]}" 2>/dev/null)" = "${identities[$2]}" ] || return 1
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
                elif [ "${present[$i]}" = 1 ]; then
                    # Gone since the app looked, as mid reinstall: what comes back there is not the install recorded.
                    echo "kcap: ${entries[$i]} is gone since the prompt" >&2
                    move_back && say {{Restored}}; exit 5
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

            # A reinstall can bring an entry or a link back while the rest is deleted: that is a new install, not this one
            # gone, so the outcome is left for the app to read from the files.
            i=0
            while [ $i -lt $nentries ]; do
                if exists "${entries[$i]}"; then echo "kcap: ${entries[$i]} came back while the install was deleted" >&2; exit 8; fi
                i=$((i + 1))
            done
            i=0
            while [ $i -lt $nlinks ]; do
                if [ "${swapped[$i]:-}" = 1 ] && [ "$(/usr/bin/readlink -- "${links[$i]}" 2>/dev/null)" != "${news[$i]}" ]; then
                    echo "kcap: ${links[$i]} changed while the install was deleted" >&2; exit 8
                fi
                i=$((i + 1))
            done
            say {{Deleted}}

            """;
    }

    /// <summary>The script's positional arguments: the status file, the control pipe, the uid that must own their
    /// folder, the staging name's random part, the folder of every account's home, the home files as a count and
    /// paths, the account registry's path or nothing, the account files, the home links and the encoded install paths,
    /// each as a count and values, then the links as a count and
    /// (path, original, replacement) triples, and the entries as a count and (original, aside, kind d/l/f, marker,
    /// pinned path, pinned identity, link target name, present 1/0, substring count, substrings…, child count or -,
    /// children…) groups, each empty check skipped.</summary>
    internal static IReadOnlyList<string> Arguments(PrivilegedSpec spec, uint owner) {
        Validate(spec);
        var args = new List<string> { spec.StatusFile, spec.ControlFifo, Number(owner), spec.Staging, spec.Homes, Number(spec.Sites.Files.Count) };
        args.AddRange(spec.Sites.Files);
        args.AddRange([spec.Sites.Registry ?? "", Number(spec.Sites.AccountFiles.Count)]);
        args.AddRange(spec.Sites.AccountFiles);
        args.Add(Number(spec.Sites.Links.Count));
        args.AddRange(spec.Sites.Links);
        args.Add(Number(spec.Needles.Count));
        args.AddRange(spec.Needles);
        args.Add(Number(spec.Links.Count));
        foreach (var link in spec.Links) args.AddRange([link.Path, link.Original, link.Replacement]);
        args.Add(Number(spec.Entries.Count));
        foreach (var entry in spec.Entries) {
            args.AddRange([
                entry.Entry.Original, entry.Entry.Aside, Letter(entry.Kind), entry.Marker ?? "",
                entry.Pinned?.Path ?? "", entry.Pinned?.Identity ?? "", entry.LinkTargetName ?? "", entry.Present ? "1" : "0",
                Number(entry.MarkerContains.Count),
            ]);
            args.AddRange(entry.MarkerContains);
            args.Add(entry.Children is { } children ? Number(children.Count) : "-");
            args.AddRange(entry.Children ?? []);
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
        foreach (var file in spec.Sites.Files.Concat(spec.Sites.AccountFiles).Concat(spec.Sites.Registry is { } r ? [r] : []))
            Require(file, Relative, "home file");
        foreach (var link in spec.Sites.Links) Require(link, l => Relative(l) || Normal(l), "kcap link");
        foreach (var needle in spec.Needles) Require(needle, _ => true, "install path");
        Require(spec.Staging, s => s.Length >= 32 && s.All(char.IsAsciiHexDigitLower), "staging name");
        if (Path.GetDirectoryName(spec.StatusFile) != Path.GetDirectoryName(spec.ControlFifo))
            throw new ArgumentException("The status file and the control pipe must share one folder.", nameof(spec));
        foreach (var link in spec.Links) {
            Require(link.Path, Path.IsPathFullyQualified, "link");
            Require(link.Original, _ => true, "link target");
            Require(link.Replacement, Path.IsPathFullyQualified, "link replacement");
        }
        foreach (var privileged in spec.Entries) {
            var (entry, kind, marker, contains, pinned, linkTarget) = privileged;
            Require(entry.Original, Normal, "entry");
            if (pinned is var (pinnedPath, identity)) {
                if (kind != PrivilegedEntryKind.Directory) throw new ArgumentException($"Only a directory holds a pinned file, not {entry.Original}.", nameof(spec));
                Require(pinnedPath, Relative, "pinned path");
                Require(identity, i => i.Split(':') is { Length: 4 } parts && parts.All(n => n.Length > 0 && n.All(char.IsAsciiDigit)), "pinned identity");
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
            foreach (var child in privileged.Children ?? []) Require(child, c => !c.Contains('/') && c is not ("." or ".."), "child name");
        }
    }

    static bool Normal(string path) => Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path;

    static bool Relative(string path) => !Path.IsPathRooted(path) && !path.Split('/').Contains("..");

    static void Require(string value, Func<string, bool> valid, string what) {
        if (string.IsNullOrEmpty(value) || value.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0 || !valid(value))
            throw new ArgumentException($"The privileged helper cannot take {what} '{value}'.");
    }
}
