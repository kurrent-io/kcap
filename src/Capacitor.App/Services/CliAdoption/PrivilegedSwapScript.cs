using System.Globalization;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>
/// The bash script one administrator prompt runs as root. It writes its pid to the status file, replaces each link
/// still holding its original target, reports <see cref="Swapped"/> and waits for one word on the control pipe. On
/// <see cref="Go"/> it sets every entry aside and checks each before deleting any, so a failed check puts the whole
/// install back. Any other word, end of file or no word in time puts the links back. After go it finishes even if the
/// app is gone.
/// <para>Every path reaches it as a positional argument, never as script text. It creates the status file itself and
/// writes only through that descriptor: root must not write through a path in a folder the user controls.</para>
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

            status=$1 control=$2 marker=$3
            shift 3
            nneed=$1; shift; k=0
            while [ $k -lt $nneed ]; do need[$k]=$1; shift; k=$((k + 1)); done
            nlinks=$1; shift; k=0
            while [ $k -lt $nlinks ]; do links[$k]=$1; olds[$k]=$2; news[$k]=$3; shift 3; k=$((k + 1)); done
            nentries=$1; shift; k=0
            while [ $k -lt $nentries ]; do entries[$k]=$1; asides[$k]=$2; shift 2; k=$((k + 1)); done

            exists() { [ -e "$1" ] || [ -L "$1" ]; }
            kind() { if [ -L "$1" ]; then echo l; elif [ -d "$1" ]; then echo d; elif [ -e "$1" ]; then echo f; else echo -; fi; }

            if exists "$status"; then echo "kcap: $status is in the way" >&2; exit 2; fi
            set -C
            exec 5>"$status" || exit 2
            set +C
            [ -f /dev/fd/5 ] || exit 2
            say() { echo "$1" >&5; }
            say $$

            [ -p "$control" ] && ! [ -L "$control" ] && exec 4<>"$control" && exec 3<"$control" && exec 4<&- && [ -p /dev/fd/3 ] \
                || { echo "kcap: $control is not the control pipe" >&2; say {{Restored}}; exit 3; }

            i=0
            while [ $i -lt $nentries ]; do kinds[$i]=$(kind "${entries[$i]}"); i=$((i + 1)); done

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
                [ "$(kind "$1")" = "$2" ] || return 1
                [ "$2" = d ] || return 0
                [ -f "$1/$marker" ] && ! [ -L "$1/$marker" ] || return 1
                local j=0
                while [ $j -lt $nneed ]; do
                    /usr/bin/grep -qF -e "${need[$j]}" -- "$1/$marker" || return 1
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
                    if ! ours "${asides[$i]}" "${kinds[$i]}"; then
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

    /// <summary>The script's positional arguments: the status file, the control pipe and the marker, then each list as
    /// its count followed by its items: the marker substrings, the links as (path, original, replacement), the entries
    /// as (original, aside).</summary>
    internal static IReadOnlyList<string> Arguments(PrivilegedSpec spec) {
        Validate(spec);
        var args = new List<string> { spec.StatusFile, spec.ControlFifo, spec.Marker, Count(spec.MarkerContains.Count) };
        args.AddRange(spec.MarkerContains);
        args.Add(Count(spec.Links.Count));
        foreach (var link in spec.Links) args.AddRange([link.Path, link.Original, link.Replacement]);
        args.Add(Count(spec.Entries.Count));
        foreach (var entry in spec.Entries) args.AddRange([entry.Original, entry.Aside]);
        return args;
    }

    static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

    // A line break would let `$(readlink)` match a target it trimmed and split a marker substring into two patterns.
    static void Validate(PrivilegedSpec spec) {
        if (spec.GoWait <= TimeSpan.Zero) throw new ArgumentException("The wait for go must be positive.", nameof(spec));

        Require(spec.ControlFifo, Path.IsPathFullyQualified, "control pipe");
        Require(spec.StatusFile, Path.IsPathFullyQualified, "status file");
        Require(spec.Marker, m => !Path.IsPathRooted(m) && !m.Split('/').Contains(".."), "marker");
        foreach (var needed in spec.MarkerContains) Require(needed, _ => true, "marker substring");
        foreach (var link in spec.Links) {
            Require(link.Path, Path.IsPathFullyQualified, "link");
            Require(link.Original, _ => true, "link target");
            Require(link.Replacement, Path.IsPathFullyQualified, "link replacement");
        }
        foreach (var entry in spec.Entries) {
            Require(entry.Original, o => Path.IsPathFullyQualified(o) && Path.GetFullPath(o) == o, "entry");
            Require(entry.Aside, a => AsideRetirement.IsOwnAside(entry.Original, a, linkAllowed: true), "aside");
        }
    }

    static void Require(string value, Func<string, bool> valid, string what) {
        if (string.IsNullOrEmpty(value) || value.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0 || !valid(value))
            throw new ArgumentException($"The privileged helper cannot take {what} '{value}'.");
    }
}
