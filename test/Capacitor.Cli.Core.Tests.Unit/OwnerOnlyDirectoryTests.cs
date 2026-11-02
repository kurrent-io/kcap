using System.Runtime.Versioning;
using System.Security.AccessControl;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit;

public class OwnerOnlyDirectoryTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task A_new_directory_admits_only_the_user_and_SYSTEM_and_ignores_its_parent() {
        var path = Tmp.PathTo("state");

        OwnerOnlyDirectory.Ensure(path);

        await Assert.That(WindowsAcl.IsProtected(path)).IsTrue();
        await AssertOwnerOnly(path);
    }

    /// <summary>The Users grant on the existing directory, and its inheritance into the file, are what
    /// make the assertions after <see cref="OwnerOnlyDirectory.Ensure"/> mean anything.</summary>
    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task An_existing_directory_and_what_it_holds_lose_every_other_grant() {
        string dir = Tmp.CreateDir("state");
        WindowsAcl.GrantUsersRead(dir);
        var file = Tmp.CreateFile(["state", "prompts", "prompt.md"], "secret");
        await Assert.That(WindowsAcl.Identities(file)).Contains(WindowsAcl.Users.Value);

        OwnerOnlyDirectory.Ensure(dir);

        await AssertOwnerOnly(dir);
        await AssertOwnerOnly(file);
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task An_existing_directory_is_made_0700() {
        string dir = Tmp.CreateDir("state");
        File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101);

        OwnerOnlyDirectory.Ensure(dir);

        await Assert.That(File.GetUnixFileMode(dir))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // ── CreateVerified: a restriction that is read back, for callers that refuse rather than warn ──

    const string User          = "S-1-5-21-1-2-3-1001";
    const string LocalSystem   = "S-1-5-18";
    const string BuiltinUsers  = "S-1-5-32-545";
    const string Everyone      = "S-1-1-0";
    static readonly string[] Owners = [User, LocalSystem];

    [Test]
    public async Task A_protected_dacl_granting_only_the_owners_is_owner_only() =>
        await Assert.That(OwnerOnlyDirectory.AclViolation(
                User, isProtected: true, [(User, true), (LocalSystem, true)], Owners))
            .IsNull();

    /// <summary>An unprotected DACL takes whatever its parent grants, so it is not owner-only even when
    /// its own entries are.</summary>
    [Test]
    public async Task An_inheriting_dacl_is_not_owner_only() =>
        await Assert.That(OwnerOnlyDirectory.AclViolation(User, isProtected: false, [(User, true)], Owners))
            .IsNotNull();

    /// <summary>Everyone is how a null DACL reads back, so this is also the no-DACL-at-all case.</summary>
    [Test]
    [Arguments(BuiltinUsers)]
    [Arguments(Everyone)]
    public async Task An_allow_for_anyone_else_is_not_owner_only(string other) =>
        await Assert.That(OwnerOnlyDirectory.AclViolation(
                User, isProtected: true, [(User, true), (other, true)], Owners))
            .Contains(other);

    [Test]
    public async Task A_deny_entry_only_narrows_access() =>
        await Assert.That(OwnerOnlyDirectory.AclViolation(
                User, isProtected: true, [(User, true), (Everyone, false)], Owners))
            .IsNull();

    /// <summary>An owner can rewrite the DACL whatever it says, so a foreign owner defeats it; an
    /// administrator could take ownership anyway, so Administrators owning it widens nothing.</summary>
    [Test]
    public async Task The_owner_must_be_the_user_SYSTEM_or_Administrators() {
        await Assert.That(OwnerOnlyDirectory.AclViolation(BuiltinUsers, true, [(User, true)], Owners)).IsNotNull();
        await Assert.That(OwnerOnlyDirectory.AclViolation(null, true, [(User, true)], Owners)).IsNotNull();
        await Assert.That(OwnerOnlyDirectory.AclViolation(LocalSystem, true, [(User, true)], Owners)).IsNull();
        await Assert.That(OwnerOnlyDirectory.AclViolation(
                OwnerOnlyDirectory.AdministratorsSid, true, [(User, true)], Owners))
            .IsNull();
    }

    /// <summary>The Users grant on the parent is what a new directory would otherwise inherit.</summary>
    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task CreateVerified_makes_a_new_directory_owner_only_whatever_its_parent_grants() {
        string parent = Tmp.CreateDir("parent");
        WindowsAcl.GrantUsersRead(parent);
        var path = Path.Combine(parent, "home");

        OwnerOnlyDirectory.CreateVerified(path);

        await Assert.That(WindowsAcl.IsProtected(path)).IsTrue();
        await AssertOwnerOnly(path);
    }

    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task CreateVerified_restricts_an_existing_directory_that_grants_others() {
        string path = Tmp.CreateDir("home");
        WindowsAcl.GrantUsersRead(path);

        OwnerOnlyDirectory.CreateVerified(path);

        await AssertOwnerOnly(path);
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task CreateVerified_makes_a_new_directory_0700() {
        var path = Tmp.PathTo("home");

        OwnerOnlyDirectory.CreateVerified(path);

        await Assert.That(File.GetUnixFileMode(path))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>An existing directory is refused rather than repaired, so a wider mode is never
    /// silently accepted.</summary>
    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task CreateVerified_refuses_an_existing_directory_wider_than_0700() {
        string path = Tmp.CreateDir("home");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);

        var ex = Assert.Throws<InvalidOperationException>(() => OwnerOnlyDirectory.CreateVerified(path));

        await Assert.That(ex!.Message).Contains("not owner-only");
    }

    [SupportedOSPlatform("windows")]
    static async Task AssertOwnerOnly(string path) {
        await Assert.That(WindowsAcl.Identities(path)).IsEquivalentTo(WindowsAcl.OwnerOnly);

        foreach (var rule in WindowsAcl.Rules(path)) {
            await Assert.That(rule.AccessControlType).IsEqualTo(AccessControlType.Allow);
            await Assert.That(rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)).IsTrue();
        }
    }
}
