using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// The local root on Windows: a protected DACL for the user and SYSTEM, which everything beneath inherits in place of
/// the <c>0700</c> root and <c>0600</c> files Unix gets. A root that cannot be secured is refused while it holds no
/// installation, since choosing another costs nothing yet; one that holds an installation is used with a warning,
/// so a root where no ACL can be set still starts.
/// </summary>
public static class LocalRootAccess {
    const string CreatorOwnerSid = "S-1-3-0";
    const string OwnerRightsSid  = "S-1-3-4";

    [SupportedOSPlatform("windows")]
    public static void Secure(LocalRoot root, TextWriter warnings) =>
        Secure(root.Directory, root.HoldsInstallation, Violation, OtherAccounts, OwnerOnlyDirectory.CreateVerified, warnings);

    /// <summary>A root already owner-only is left alone: re-applying the DACL propagates it through the whole tree,
    /// Postgres's data included, at every lifecycle command.</summary>
    /// <param name="others">Accounts besides the user, SYSTEM and Administrators the directory admits, read before
    /// it is secured so the warning can name who could reach the installation.</param>
    internal static void Secure(
            string directory, bool installed, Func<string, string?> violation, Func<string, IReadOnlyList<string>> others,
            Action<string> secure, TextWriter warnings) {
        var exists = Directory.Exists(directory);
        if (exists && violation(directory) is null) return;

        IReadOnlyList<string> exposed = exists && installed ? others(directory) : [];

        try {
            secure(directory);
        } catch (Exception ex) {
            if (!installed)
                throw new LocalLifecycleException(
                    $"{directory} cannot be made private to you ({ex.Message}), so other accounts on this PC could read the local installation. "
                  + $"Set {LocalRoot.EnvVar} to a directory under your user profile, or to a new one on an NTFS drive, and run setup again.", ex);

            warnings.WriteLine(
                $"kcap: warning: could not make the local installation's directory {directory} private to you ({ex.GetType().Name}: {ex.Message}); "
              + "its contents keep the access its location grants.");
            return;
        }

        if (exposed.Count > 0)
            warnings.WriteLine(
                $"kcap: warning: the local installation's directory {directory} was open to {string.Join(", ", exposed)}; it is now private to you.");
    }

    /// <summary>Why the root's owner can rewrite its DACL against the user, or null when it cannot: the owner is
    /// the user, SYSTEM or Administrators. A descriptor that cannot be read counts as foreign.</summary>
    [SupportedOSPlatform("windows")]
    public static string? ForeignOwner(string directory) {
        string? owner;

        try {
            owner = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))?.Value;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
            return $"its owner cannot be read ({ex.Message})";
        }

        return OwnerOnlyDirectory.AclViolation(owner, isProtected: true, [], OwnerOnlyDirectory.OwnerSids());
    }

    [SupportedOSPlatform("windows")]
    static string? Violation(string directory) {
        try {
            return OwnerOnlyDirectory.Violation(directory);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
            return $"its DACL cannot be read ({ex.Message})";
        }
    }

    [SupportedOSPlatform("windows")]
    static IReadOnlyList<string> OtherAccounts(string directory) {
        var expected = new HashSet<string>(
            [.. OwnerOnlyDirectory.OwnerSids(), OwnerOnlyDirectory.AdministratorsSid, CreatorOwnerSid, OwnerRightsSid],
            StringComparer.OrdinalIgnoreCase);

        try {
            return [.. new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(rule => rule.AccessControlType == AccessControlType.Allow && !expected.Contains(rule.IdentityReference.Value))
                .Select(rule => AccountName((SecurityIdentifier)rule.IdentityReference))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    static string AccountName(SecurityIdentifier sid) {
        try {
            return sid.Translate(typeof(NTAccount)).Value;
        } catch (SystemException) {
            return sid.Value;
        }
    }
}
