using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>
/// Whether another local account could rename or replace an entry on the path to a directory, from it up to
/// the volume root, with every symlink or junction on the way resolved and its target's chain walked too.
/// Fails closed: an entry that is missing, or whose owner or DACL cannot be read for any reason, is a
/// violation, and no check throws.
/// </summary>
internal static class AncestorChain {
    const string CreatorOwnerSid    = "S-1-3-0";
    const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    const int DeleteChild = 0x40;
    const int Delete      = 0x10000;
    const int WriteDac    = 0x40000;
    const int WriteOwner  = 0x80000;
    const int GenericAll  = 0x10000000;

    /// <summary>The first violation found, nearest first, or null when the chain is private. Unix: every
    /// directory owned by this user or root and not writable by group or others unless sticky. Windows: every
    /// directory owned by a trusted account, and no other account holding a right that can replace the next
    /// entry down.</summary>
    public static string? Violation(string path) {
        try {
            return OperatingSystem.IsWindows() ? WindowsViolation(path) : UnixViolation(path);
        } catch (Exception ex) {
            return $"the chain above '{path}' cannot be checked: {ex.Message}";
        }
    }

    /// <summary>The Windows rule over one directory's owner and DACL, as SID strings and raw ACE masks so it
    /// runs on any host. An untrusted owner holds an implicit WRITE_DAC. Delete-child and the ACL rights
    /// count everywhere; delete only where the directory has a parent to be removed from. Inherit-only
    /// entries do not apply to the directory itself, and deny entries only narrow.</summary>
    public static string? WindowsAclViolation(
            string? owner, IEnumerable<(string Sid, bool Allows, bool InheritOnly, int Rights)> entries, bool hasParent,
            IReadOnlyCollection<string> ownerSids) {
        if (owner is null || !IsTrusted(owner, ownerSids)) return $"it is owned by {owner ?? "nobody"}";

        var forbidden = DeleteChild | WriteDac | WriteOwner | (hasParent ? Delete : 0);

        try {
            foreach (var (sid, allows, inheritOnly, rights) in entries) {
                if (!allows || inheritOnly || IsTrusted(sid, ownerSids)) continue;

                var effective = (rights & GenericAll) != 0 ? forbidden : rights & forbidden;

                if (effective != 0)
                    return $"it grants {sid} rights 0x{effective.ToString("X", CultureInfo.InvariantCulture)}";
            }
        } catch (Exception ex) {
            return $"its DACL cannot be read: {ex.Message}";
        }

        return null;
    }

    /// <summary>The Unix owner rule: every entry on the chain exists and is owned by <paramref name="self"/>
    /// or root. A link outside a sticky directory is skipped, its holder's own check covering it, but an
    /// owner that cannot be read is a violation.</summary>
    public static string? UnixOwnerViolation(string path, Func<string, uint?> ownerOf, uint self) {
        foreach (var dir in DirectoryExposure.Ancestors(path)) {
            if (!Directory.Exists(dir)) return $"'{dir}' is missing";
            if (DirectoryExposure.IsLinkOutsideStickyDirectory(dir)) continue;

            if (OwnerOf(ownerOf, dir) is not { } owner) return $"the owner of '{dir}' cannot be read";
            if (owner != 0 && owner != self) return $"'{dir}' is owned by uid {owner}";
        }

        foreach (var link in DirectoryExposure.Links(path)) {
            if (OwnerOf(ownerOf, link) is not { } owner) return $"the owner of the link '{link}' cannot be read";
            if (owner != 0 && owner != self) return $"the link '{link}' is owned by uid {owner}";
        }

        return null;
    }

    static uint? OwnerOf(Func<string, uint?> ownerOf, string path) {
        try {
            return ownerOf(path);
        } catch (Exception) {
            return null;
        }
    }

    static bool IsTrusted(string sid, IReadOnlyCollection<string> ownerSids) =>
        ownerSids.Contains(sid, StringComparer.OrdinalIgnoreCase)
     || string.Equals(sid, OwnerOnlyDirectory.AdministratorsSid, StringComparison.OrdinalIgnoreCase)
     || string.Equals(sid, TrustedInstallerSid, StringComparison.OrdinalIgnoreCase)
     || string.Equals(sid, CreatorOwnerSid, StringComparison.OrdinalIgnoreCase);

    [UnsupportedOSPlatform("windows")]
    static string? UnixViolation(string path) {
        if (!FileOwner.Available) return "no system `stat` command can read directory owners";

        if (DirectoryExposure.GrantingWrite(path, UnixFileMode.GroupWrite | UnixFileMode.OtherWrite) is [var shared, ..])
            return $"'{shared}' is writable by group or others and not sticky";

        return UnixOwnerViolation(path, FileOwner.Of, FileOwner.Effective);
    }

    [SupportedOSPlatform("windows")]
    static string? WindowsViolation(string path) {
        var owners = OwnerOnlyDirectory.OwnerSids();

        foreach (var dir in DirectoryExposure.Ancestors(path)) {
            if (!Directory.Exists(dir)) return $"'{dir}' is missing";

            DirectorySecurity security;

            try {
                security = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            } catch (Exception ex) {
                return $"the DACL of '{dir}' cannot be read: {ex.Message}";
            }

            var entries = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Select(rule => (rule.IdentityReference.Value, rule.AccessControlType == AccessControlType.Allow,
                    rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly), (int)rule.FileSystemRights));

            string? owner;

            try {
                owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
            } catch (Exception ex) {
                return $"the owner of '{dir}' cannot be read: {ex.Message}";
            }

            if (WindowsAclViolation(owner, entries, hasParent: Path.GetDirectoryName(dir) is not null, owners) is { } violation)
                return $"'{dir}': {violation}";
        }

        foreach (var link in DirectoryExposure.Links(path)) {
            string? owner;

            try {
                owner = new DirectoryInfo(link).GetAccessControl(AccessControlSections.Owner)
                    .GetOwner(typeof(SecurityIdentifier))?.Value;
            } catch (Exception ex) {
                return $"the owner of the link '{link}' cannot be read: {ex.Message}";
            }

            if (owner is null || !IsTrusted(owner, owners)) return $"the link '{link}' is owned by {owner ?? "nobody"}";
        }

        return null;
    }
}
