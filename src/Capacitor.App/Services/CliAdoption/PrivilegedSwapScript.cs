using System.Globalization;
using Capacitor.Cli.Core.Install;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>
/// The bash script one administrator prompt runs as root. It writes its pid to the status file, replaces each link
/// still holding its original target, reports <see cref="Swapped"/> and waits for one word on the control pipe. On
/// <see cref="Go"/> it sets every entry aside and checks each against its expected kind and its own marker before
/// deleting any, so a failed check puts the whole install back. Any other word, end of file or no word in time puts
/// the links back. After go it finishes even if the app is gone.
/// <para>Every path reaches it as a positional argument, never as script text. It creates the status file itself, only
/// inside a real 0700 folder the app's user owns, and writes only through that descriptor: root must not write through
/// a path someone else controls.</para>
/// </summary>
internal static class PrivilegedSwapScript {
    internal const string Swapped  = "swapped";
    internal const string Restored = "restored";
    internal const string Deleted  = "deleted";
    internal const string Go       = "go";
    internal const string PutBack  = "put-back";

    internal static string Render(PrivilegedSpec spec) {
        Validate(spec);
        var wait = Math.Max(1, (int)Math.Ceiling(spec.GoWait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return $$"""
            #!/bin/bash
            set -u
            trap '' HUP PIPE
            umask 022

            status=$1 control=$2 owner=$3
            shift 3
            nlinks=$1; shift; k=0
            while [ $k -lt $nlinks ]; do links[$k]=$1; olds[$k]=$2; news[$k]=$3; shift 3; k=$((k + 1)); done
            nentries=$1; shift; k=0; m=0
            while [ $k -lt $nentries ]; do
                entries[$k]=$1; asides[$k]=$2; kinds[$k]=$3; markers[$k]=$4; first[$k]=$m; c=$5; shift 5
                while [ $c -gt 0 ]; do needs[$m]=$1; shift; m=$((m + 1)); c=$((c - 1)); done
                last[$k]=$m
                k=$((k + 1))
            done

            exists() { [ -e "$1" ] || [ -L "$1" ]; }
            kind() { if [ -L "$1" ]; then echo l; elif [ -d "$1" ]; then echo d; elif [ -e "$1" ]; then echo f; else echo -; fi; }

            if exists "$status"; then echo "kcap: $status is in the way" >&2; exit 2; fi
            # The folder can still change before the open below; the file is only ever created, with fixed content.
            dir=${status%/*}
            [ -d "$dir" ] && ! [ -L "$dir" ] && [ "$(/usr/bin/stat -f '%u %Lp' -- "$dir")" = "$owner 700" ] \
                || { echo "kcap: $dir is not a private folder of user $owner" >&2; exit 2; }
            set -C
            exec 5>"$status" || exit 2
            set +C
            [ -f /dev/fd/5 ] || exit 2
            say() { echo "$1" >&5; }
            say $$

            [ -p "$control" ] && ! [ -L "$control" ] && exec 4<>"$control" && exec 3<"$control" && exec 4<&- && [ -p /dev/fd/3 ] \
                || { echo "kcap: $control is not the control pipe" >&2; say {{Restored}}; exit 3; }

            relink() {
                [ -L "$1" ] && [ "$(/usr/bin/readlink -- "$1")" = "$2" ] || return 1
                /bin/ln -s -- "$3" "$1.kcap-swap-$$" || return 1
                /bin/mv -fh -- "$1.kcap-swap-$$" "$1" && return 0
                /bin/rm -f -- "$1.kcap-swap-$$"
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

            move_back() {
                local i=$nentries back=0
                while [ $i -gt 0 ]; do
                    i=$((i - 1))
                    [ "${moved[$i]:-}" = 1 ] || continue
                    if exists "${entries[$i]}" || ! /bin/mv -h -- "${asides[$i]}" "${entries[$i]}"; then
                        echo "kcap: left ${entries[$i]} at ${asides[$i]}" >&2
                        back=1
                    fi
                done
                put_back
                return $back
            }

            ours() {
                [ "$(kind "$1")" = "${kinds[$2]}" ] || return 1
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
                if exists "${entries[$i]}"; then
                    if exists "${asides[$i]}" || ! /bin/mv -h -- "${entries[$i]}" "${asides[$i]}"; then
                        echo "kcap: could not set ${entries[$i]} aside" >&2
                        move_back && say {{Restored}}; exit 5
                    fi
                    moved[$i]=1
                    if ! ours "${asides[$i]}" $i; then
                        echo "kcap: ${entries[$i]} is no longer the install that was recorded" >&2
                        move_back && say {{Restored}}; exit 5
                    fi
                fi
                i=$((i + 1))
            done

            left=0
            i=0
            while [ $i -lt $nentries ]; do
                if [ -L "${asides[$i]}" ]; then /bin/rm -f -- "${asides[$i]}"
                elif [ -e "${asides[$i]}" ]; then /bin/rm -rf -- "${asides[$i]}"
                fi
                exists "${asides[$i]}" && left=1
                i=$((i + 1))
            done
            [ $left = 0 ] || { echo "kcap: part of the install could not be deleted" >&2; exit 6; }
            say {{Deleted}}

            """;
    }

    /// <summary>The script's positional arguments: the status file, the control pipe, the uid that must own their
    /// folder, then the links as a count and (path, original, replacement) triples, and the entries as a count and
    /// (original, aside, kind d/l/f, marker, substring count, substrings…) groups, an empty marker meaning a kind check
    /// only.</summary>
    internal static IReadOnlyList<string> Arguments(PrivilegedSpec spec, uint owner) {
        Validate(spec);
        var args = new List<string> { spec.StatusFile, spec.ControlFifo, Number(owner), Number(spec.Links.Count) };
        foreach (var link in spec.Links) args.AddRange([link.Path, link.Original, link.Replacement]);
        args.Add(Number(spec.Entries.Count));
        foreach (var entry in spec.Entries) {
            args.AddRange([entry.Entry.Original, entry.Entry.Aside, Letter(entry.Kind), entry.Marker ?? "", Number(entry.MarkerContains.Count)]);
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
        if (Path.GetDirectoryName(spec.StatusFile) != Path.GetDirectoryName(spec.ControlFifo))
            throw new ArgumentException("The status file and the control pipe must share one folder.", nameof(spec));
        foreach (var link in spec.Links) {
            Require(link.Path, Path.IsPathFullyQualified, "link");
            Require(link.Original, _ => true, "link target");
            Require(link.Replacement, Path.IsPathFullyQualified, "link replacement");
        }
        foreach (var (entry, kind, marker, contains) in spec.Entries) {
            Require(entry.Original, Normal, "entry");
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
