using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Capacitor.Cli.Core;

/// <summary><c>0700</c> on Unix; on Windows a protected DACL, inherited beneath it, for the current user and
/// SYSTEM. SYSTEM stays, as root does under <c>0700</c>: it can take any file anyway, and backup and
/// antivirus run as it.</summary>
public static class OwnerOnlyDirectory {
    const UnixFileMode OwnerOnlyMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>BUILTIN\Administrators, the default owner of what an elevated administrator creates.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>Creates <paramref name="path"/> if missing and restricts it. On Windows what inherits from
    /// it inside gets the new DACL too; an explicit grant on an entry survives. Throws on failure.</summary>
    public static void Ensure(string path) {
        if (OperatingSystem.IsWindows()) {
            Directory.CreateDirectory(path).SetAccessControl(OwnerOnlySecurity());
            return;
        }

        Directory.CreateDirectory(path, OwnerOnlyMode);
        File.SetUnixFileMode(path, OwnerOnlyMode);
    }

    /// <summary>
    /// Creates <paramref name="path"/> owner-only from its first instant, then reads the restriction back
    /// and throws <see cref="InvalidOperationException"/> when it does not hold. For a directory whose
    /// contents must never be readable by another local user, where a caller refuses rather than warns.
    ///
    /// <para>Unix leaves an existing directory's mode alone and refuses it if it is wider than
    /// <c>0700</c>. Windows re-applies the DACL to an existing directory, then checks it.</para>
    /// </summary>
    public static void CreateVerified(string path) {
        if (OperatingSystem.IsWindows()) {
            CreateVerifiedWindows(path);
            return;
        }

        Directory.CreateDirectory(path, OwnerOnlyMode);

        // The mode argument applies only when CreateDirectory creates, and a filesystem may ignore it.
        var mode = File.GetUnixFileMode(path);

        if ((mode & GroupOrOther) != 0)
            throw new InvalidOperationException($"'{path}' is mode {mode}, not owner-only.");
    }

    /// <summary>Why a security descriptor read back from a directory is not owner-only, or null when it
    /// is: its owner must be one of <paramref name="ownerSids"/> or Administrators (an owner can rewrite
    /// the DACL, and an administrator can take ownership anyway), its DACL must be protected from
    /// inheritance, and every allow entry must name one of <paramref name="ownerSids"/>. Deny entries
    /// only narrow access, so they pass. A null DACL reads back as an allow for Everyone, so it fails
    /// too. Takes SID strings so the rule runs on any host.</summary>
    public static string? AclViolation(
            string? owner, bool isProtected, IEnumerable<(string Sid, bool Allows)> entries,
            IReadOnlyCollection<string> ownerSids) {
        if (owner is null
         || (!ownerSids.Contains(owner, StringComparer.OrdinalIgnoreCase)
          && !string.Equals(owner, AdministratorsSid, StringComparison.OrdinalIgnoreCase)))
            return $"it is owned by {owner ?? "nobody"}";

        if (!isProtected) return "its DACL inherits from its parent";

        foreach (var (sid, allows) in entries) {
            if (allows && !ownerSids.Contains(sid, StringComparer.OrdinalIgnoreCase))
                return $"its DACL grants {sid}";
        }

        return null;
    }

    /// <summary>Drops every explicit grant on <paramref name="file"/>, leaving only what it inherits from
    /// its directory. For a file moved in, whose own ACL <see cref="Ensure"/> does not touch.</summary>
    [SupportedOSPlatform("windows")]
    public static void InheritOnly(string file) {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
        new FileInfo(file).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    static void CreateVerifiedWindows(string path) {
        var info     = new DirectoryInfo(path);
        var security = OwnerOnlySecurity();

        // Created separately so the owner-only descriptor lands on this directory alone, not on every
        // missing ancestor the descriptor-carrying create would also make.
        if (info.Parent is { } parent) Directory.CreateDirectory(parent.FullName);

        // A no-op when the directory already exists, so the set below covers that case and a race.
        info.Create(security);
        info.SetAccessControl(security);

        var readBack = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var owner    = readBack.GetOwner(typeof(SecurityIdentifier))?.Value;
        var entries  = readBack.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => (rule.IdentityReference.Value, rule.AccessControlType == AccessControlType.Allow));

        if (AclViolation(owner, readBack.AreAccessRulesProtected, entries, OwnerSids()) is { } violation)
            throw new InvalidOperationException($"'{path}' is not owner-only: {violation}.");
    }

    [SupportedOSPlatform("windows")]
    static string[] OwnerSids() => [
        WindowsIdentity.GetCurrent().User!.Value,
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
    ];

    [SupportedOSPlatform("windows")]
    static DirectorySecurity OwnerOnlySecurity() {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sid in OwnerSids()) {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }
}
