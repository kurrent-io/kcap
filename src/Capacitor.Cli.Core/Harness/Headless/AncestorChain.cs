using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>
/// Whether another local account could rename or replace an entry on the path to a directory, from it up to
/// the volume root, with every symlink or junction on the way resolved and its target's chain walked too.
/// </summary>
internal static class AncestorChain {
    const string CreatorOwnerSid = "S-1-3-0";

    const int DeleteChild = 0x40;
    const int Delete      = 0x10000;
    const int WriteDac    = 0x40000;
    const int WriteOwner  = 0x80000;
    const int GenericAll  = 0x10000000;

    /// <summary>The first violation found, nearest first, or null when the chain is private. Unix: every
    /// directory owned by this user or root and not writable by group or others unless sticky. Windows: no
    /// account but the owners, Administrators and <c>CREATOR OWNER</c> holds a right that can replace the
    /// next entry down.</summary>
    public static string? Violation(string path) =>
        OperatingSystem.IsWindows() ? WindowsViolation(path) : UnixViolation(path);

    /// <summary>The Windows rule over one directory's DACL, as raw ACE masks so it runs on any host.
    /// Delete-child and the ACL rights count everywhere; delete only where the directory has a parent to be
    /// removed from. Inherit-only entries do not apply to the directory itself, and deny entries only
    /// narrow.</summary>
    public static string? WindowsAclViolation(
            IEnumerable<(string Sid, bool Allows, bool InheritOnly, int Rights)> entries, bool hasParent,
            IReadOnlyCollection<string> ownerSids) {
        var forbidden = DeleteChild | WriteDac | WriteOwner | (hasParent ? Delete : 0);

        foreach (var (sid, allows, inheritOnly, rights) in entries) {
            if (!allows || inheritOnly || IsTrusted(sid, ownerSids)) continue;

            var effective = (rights & GenericAll) != 0 ? forbidden : rights & forbidden;

            if (effective != 0)
                return $"it grants {sid} rights 0x{effective.ToString("X", CultureInfo.InvariantCulture)}";
        }

        return null;
    }

    static bool IsTrusted(string sid, IReadOnlyCollection<string> ownerSids) =>
        ownerSids.Contains(sid, StringComparer.OrdinalIgnoreCase)
     || string.Equals(sid, OwnerOnlyDirectory.AdministratorsSid, StringComparison.OrdinalIgnoreCase)
     || string.Equals(sid, CreatorOwnerSid, StringComparison.OrdinalIgnoreCase);

    [UnsupportedOSPlatform("windows")]
    static string? UnixViolation(string path) {
        if (!FileOwner.Available) return "no system `stat` command can read directory owners";

        if (DirectoryExposure.GrantingWrite(path, UnixFileMode.GroupWrite | UnixFileMode.OtherWrite) is [var shared, ..])
            return $"'{shared}' is writable by group or others and not sticky";

        if (DirectoryExposure.ForeignOwned(path) is [var (foreign, owner), ..])
            return $"'{foreign}' is owned by uid {owner}";

        foreach (var link in DirectoryExposure.Links(path)) {
            if (FileOwner.Of(link) is { } linkOwner && linkOwner != 0 && linkOwner != FileOwner.Effective)
                return $"the link '{link}' is owned by uid {linkOwner}";
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    static string? WindowsViolation(string path) {
        var owners = OwnerOnlyDirectory.OwnerSids();

        foreach (var dir in DirectoryExposure.Ancestors(path)) {
            if (!Directory.Exists(dir)) continue;

            var entries = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Select(rule => (rule.IdentityReference.Value, rule.AccessControlType == AccessControlType.Allow,
                    rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly), (int)rule.FileSystemRights));

            if (WindowsAclViolation(entries, hasParent: Path.GetDirectoryName(dir) is not null, owners) is { } violation)
                return $"'{dir}': {violation}";
        }

        foreach (var link in DirectoryExposure.Links(path)) {
            var owner = new DirectoryInfo(link).GetAccessControl(AccessControlSections.Owner)
                .GetOwner(typeof(SecurityIdentifier))?.Value;

            if (owner is null || !IsTrusted(owner, owners)) return $"the link '{link}' is owned by {owner ?? "nobody"}";
        }

        return null;
    }
}
