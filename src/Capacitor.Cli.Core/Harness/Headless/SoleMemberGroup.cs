using System.Globalization;

namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>Whether <c>/etc/group</c> and <c>/etc/passwd</c> prove a group holds this user and nobody else, as a
/// user-private group does. Anything they cannot prove is a refusal: a gid they do not list may come from LDAP or
/// another directory service, and a <c>+</c> or <c>-</c> line includes one.</summary>
internal static class SoleMemberGroup {
    /// <summary>Why <paramref name="gid"/> is not proven to hold only <paramref name="self"/>, or null when it is.
    /// Compares uids rather than names, since two account names can share a uid. A null file could not be read.</summary>
    public static string? Violation(uint gid, uint self, string? groupFile, string? passwdFile) {
        if (groupFile is null) return "/etc/group cannot be read";
        if (passwdFile is null) return "/etc/passwd cannot be read";

        var selfNames = new HashSet<string>(StringComparer.Ordinal);
        var isPrimary = false;

        foreach (var line in Lines(passwdFile)) {
            if (line.Split(':') is not [var name, _, var uidText, var gidText, _, _, _]
             || line[0] is '+' or '-'
             || !TryParse(uidText, out var uid) || !TryParse(gidText, out var primary))
                return "/etc/passwd has a line that cannot be read as a local account";

            if (uid == self) selfNames.Add(name);

            if (primary != gid) continue;
            if (uid != self) return $"account '{name}' has it as its primary group";

            isPrimary = true;
        }

        var listed   = false;
        var isMember = false;

        foreach (var line in Lines(groupFile)) {
            if (line.Split(':') is not [_, _, var gidText, var members] || line[0] is '+' or '-' || !TryParse(gidText, out var entry))
                return "/etc/group has a line that cannot be read as a local group";

            if (entry != gid) continue;

            listed = true;

            foreach (var member in members.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
                if (!selfNames.Contains(member)) return $"'{member}' is a member";

                isMember = true;
            }
        }

        if (!listed) return "/etc/group does not list it";

        return isPrimary || isMember ? null : "this user is not shown to be in it";
    }

    static IEnumerable<string> Lines(string file) =>
        file.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(line => line[0] != '#');

    static bool TryParse(string text, out uint value) => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
