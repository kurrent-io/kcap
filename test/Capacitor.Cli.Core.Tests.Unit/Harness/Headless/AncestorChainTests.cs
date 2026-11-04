using System.Runtime.Versioning;
using Capacitor.Cli.Core.Harness.Headless;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Headless;

public class AncestorChainTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string User          = "S-1-5-21-1-2-3-1001";
    const string LocalSystem   = "S-1-5-18";
    const string CreatorOwner  = "S-1-3-0";
    const string BuiltinUsers  = "S-1-5-32-545";
    const string AuthUsers     = "S-1-5-11";
    const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // Raw ACE masks: FileSystemRights is annotated Windows-only, and the rule runs on every host.
    public const int DeleteChild  = 0x40;
    public const int Delete       = 0x10000;
    public const int WriteDac     = 0x40000;
    public const int WriteOwner   = 0x80000;
    public const int Modify       = 0x301BF;
    public const int FullControl  = 0x1F01FF;
    public const int GenericAll   = 0x10000000;
    public const int Write        = 0x116;
    public const int GenericWrite = 0x40000000;
    public const int AddFolderAddFileReadExecute = 0x4 | 0x2 | 0x200A9;
    static readonly string[] Owners = [User, LocalSystem];

    static string? Rule(bool hasParent, params (string Sid, bool Allows, bool InheritOnly, int Rights)[] entries) =>
        AncestorChain.WindowsAclViolation(User, entries, hasParent, Owners);

    [Test]
    [Arguments(DeleteChild)]
    [Arguments(WriteDac)]
    [Arguments(WriteOwner)]
    [Arguments(Delete)]
    [Arguments(Modify)]
    [Arguments(FullControl)]
    [Arguments(GenericAll)]
    public async Task A_right_that_can_replace_the_next_entry_granted_to_another_account_is_a_violation(int rights) =>
        await Assert.That(Rule(hasParent: true, (BuiltinUsers, true, false, rights))).Contains(BuiltinUsers);

    [Test]
    [Arguments(AddFolderAddFileReadExecute)]
    [Arguments(Write)]
    [Arguments(GenericWrite)]
    public async Task Creating_or_writing_without_delete_or_acl_rights_is_allowed(int rights) =>
        await Assert.That(Rule(hasParent: true, (BuiltinUsers, true, false, rights))).IsNull();

    /// <summary>A volume root's usual DACL: Users may add folders to it, and Authenticated Users' Modify is
    /// inherit-only, applying to what is beneath rather than the root itself.</summary>
    [Test]
    public async Task A_volume_root_granting_create_folder_and_inherit_only_modify_is_allowed() =>
        await Assert.That(Rule(hasParent: false,
                (BuiltinUsers, true, false, AddFolderAddFileReadExecute),
                (AuthUsers, true, true, Modify)))
            .IsNull();

    [Test]
    public async Task Delete_on_a_volume_root_replaces_nothing() =>
        await Assert.That(Rule(hasParent: false, (BuiltinUsers, true, false, Delete))).IsNull();

    [Test]
    [Arguments(User)]
    [Arguments(LocalSystem)]
    [Arguments(OwnerOnlyDirectory.AdministratorsSid)]
    [Arguments(CreatorOwner)]
    public async Task Full_control_for_a_trusted_account_is_allowed(string sid) =>
        await Assert.That(Rule(hasParent: true, (sid, true, false, FullControl))).IsNull();

    [Test]
    public async Task A_deny_entry_is_not_a_grant() =>
        await Assert.That(Rule(hasParent: true, (BuiltinUsers, false, false, FullControl))).IsNull();

    /// <summary>An owner can rewrite the DACL whatever it says, so an untrusted owner fails a clean DACL.</summary>
    [Test]
    [Arguments(BuiltinUsers)]
    [Arguments(null)]
    public async Task An_untrusted_or_unreadable_owner_is_a_violation(string? owner) =>
        await Assert.That(AncestorChain.WindowsAclViolation(owner, [], hasParent: true, Owners)).IsNotNull();

    [Test]
    [Arguments(User)]
    [Arguments(LocalSystem)]
    [Arguments(OwnerOnlyDirectory.AdministratorsSid)]
    [Arguments(TrustedInstaller)]
    public async Task A_trusted_owner_is_allowed(string owner) =>
        await Assert.That(AncestorChain.WindowsAclViolation(owner, [], hasParent: false, Owners)).IsNull();

    [Test]
    public async Task An_ancestor_whose_owner_cannot_be_read_is_a_violation() {
        var leaf    = Tmp.CreateDir("a", "leaf").Path;
        var blind   = Path.GetFullPath(Tmp.PathTo("a"));
        var physical = DirectoryExposure.Physical(blind);

        var violation = AncestorChain.UnixOwnerViolation(leaf, d => d == blind || d == physical ? null : 7u, self: 7);

        await Assert.That(violation).IsNotNull();
        await Assert.That(violation!).Contains("cannot be read");
    }

    [Test]
    public async Task An_owner_read_that_throws_is_a_violation_not_an_exception() {
        var leaf = Tmp.CreateDir("a", "leaf").Path;

        var violation = AncestorChain.UnixOwnerViolation(leaf, _ => throw new InvalidOperationException("no stat"), self: 7);

        await Assert.That(violation).IsNotNull();
        await Assert.That(violation!).Contains("cannot be read");
    }

    /// <summary>The DACL's entries are read lazily, so a failure translating one surfaces inside the rule.</summary>
    [Test]
    public async Task A_dacl_whose_entries_throw_while_read_is_a_violation_not_an_exception() {
        static IEnumerable<(string, bool, bool, int)> Throwing() {
            yield return (User, true, false, FullControl);
            throw new InvalidOperationException("identity not mapped");
        }

        await Assert.That(AncestorChain.WindowsAclViolation(User, Throwing(), hasParent: true, Owners)).Contains("cannot be read");
    }

    [Test]
    public async Task A_control_chain_whose_every_owner_reads_as_this_user_passes() =>
        await Assert.That(AncestorChain.UnixOwnerViolation(Tmp.CreateDir("a", "leaf").Path, _ => 7u, self: 7)).IsNull();

    [Test]
    public async Task A_missing_entry_on_the_chain_is_a_violation() =>
        await Assert.That(AncestorChain.UnixOwnerViolation(Tmp.PathTo("gone", "leaf"), _ => 7u, self: 7))
            .IsNotNull();

    [Test]
    public async Task An_ancestor_owned_by_another_uid_is_a_violation() {
        var leaf  = Tmp.CreateDir("a", "leaf").Path;
        var other = DirectoryExposure.Physical(Tmp.PathTo("a"));

        await Assert.That(AncestorChain.UnixOwnerViolation(leaf, d => d == other ? 9u : 7u, self: 7))
            .IsEqualTo($"'{other}' is owned by uid 9");
    }

    const uint Self = 1000;

    const string Passwd = """
        ## a comment, as macOS has
        root:x:0:0:root:/root:/bin/sh
        alice:x:1000:1000:Alice:/home/alice:/bin/sh
        bob:x:1001:1001:Bob:/home/bob:/bin/sh
        """;

    const string Group = """
        root:x:0:
        alice:x:1000:
        bob:x:1001:
        team:x:2000:alice,bob
        solo:x:2001:alice
        empty:x:2002:
        """;

    [Test]
    [Arguments(1000u, Group, Passwd, null)]                                         // user-private: primary, no members
    [Arguments(2001u, Group, Passwd, null)]                                         // listed member and nobody else
    [Arguments(2000u, Group, Passwd, "'bob' is a member")]
    [Arguments(1001u, Group, Passwd, "'bob' has it as its primary group")]
    [Arguments(0u, Group, Passwd, "'root' has it as its primary group")]
    [Arguments(3000u, Group, Passwd, "does not list it")]                           // an LDAP or other NSS group
    [Arguments(2002u, Group, Passwd, "not shown to be in it")]
    [Arguments(1000u, null, Passwd, "/etc/group cannot be read")]
    [Arguments(1000u, Group, null, "/etc/passwd cannot be read")]
    [Arguments(1000u, "+:::\n" + Group, Passwd, "/etc/group has a line")]            // includes a directory service
    [Arguments(1000u, Group, "+::::::\n" + Passwd, "/etc/passwd has a line")]
    [Arguments(1000u, "alice:x:notanumber:\n", Passwd, "/etc/group has a line")]
    [Arguments(1000u, Group, "alice:x:1000\n", "/etc/passwd has a line")]
    public async Task A_group_counts_as_private_only_when_the_account_files_prove_this_user_is_alone_in_it(
            uint gid, string? groupFile, string? passwdFile, string? why) {
        var violation = SoleMemberGroup.Violation(gid, Self, groupFile, passwdFile);

        if (why is null) await Assert.That(violation).IsNull();
        else await Assert.That(violation!).Contains(why);
    }

    [Test]
    public async Task Two_account_names_on_this_uid_are_one_user() =>
        await Assert.That(SoleMemberGroup.Violation(2003, Self, "pair:x:2003:alice,alias\n",
            "alice:x:1000:1000::/h:/s\nalias:x:1000:1000::/h:/s\n")).IsNull();

    /// <summary>The gid and account files are injected over real directory modes: the bits are what is under test,
    /// and the same proof that admits the group must not admit the others bit.</summary>
    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    [Arguments(0b111_111_101, 1000u, null)]
    [Arguments(0b111_111_101, 2000u, "writable by group 2000")]
    [Arguments(0b111_111_101, 3000u, "writable by group 3000")]
    [Arguments(0b111_111_111, 1000u, "writable by others")]
    public async Task The_write_rule_admits_a_group_writable_directory_only_on_that_proof(int mode, uint gid, string? why) {
        var shared = Tmp.CreateDir("shared").Path;
        var leaf   = Tmp.CreateDir("shared", "leaf").Path;
        File.SetUnixFileMode(shared, (UnixFileMode)mode);

        var violation = AncestorChain.UnixWriteViolation(leaf, _ => gid, Self, Group, Passwd);

        if (why is null) await Assert.That(violation).IsNull();
        else await Assert.That(violation!).Contains(why);
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task A_group_writable_directory_whose_gid_cannot_be_read_is_a_violation() {
        var shared = Tmp.CreateDir("shared").Path;
        File.SetUnixFileMode(shared, (UnixFileMode)0b111_111_101);

        await Assert.That(AncestorChain.UnixWriteViolation(Tmp.CreateDir("shared", "leaf").Path, _ => null, Self, Group, Passwd)!)
            .Contains("its group cannot be read");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task The_gid_is_read_from_the_system() {
        await Assert.That(FileOwner.GroupOf("/")).IsEqualTo(0u);
        await Assert.That(FileOwner.GroupOf(Tmp.PathTo("missing"))).IsNull();
    }

    /// <summary>The link resolves into a world-writable, non-sticky directory: the lexical path is all
    /// owner-only, so only walking the target's chain finds it.</summary>
    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task A_link_in_the_chain_is_checked_through_its_target() {
        var shared = Tmp.CreateDir("shared").Path;
        var target = Tmp.CreateDir("shared", "target").Path;
        Tmp.CreateDir("shared", "target", "leaf");
        File.SetUnixFileMode(shared, (UnixFileMode)0b111_111_111);
        var link = Tmp.PathTo("link");
        Directory.CreateSymbolicLink(link, target);

        var violation = AncestorChain.Violation(Path.Combine(link, "leaf"));

        await Assert.That(violation).IsNotNull();
        await Assert.That(violation!).Contains(Tmp.GetResolvedPath("shared"));
    }

    [Test]
    public async Task A_link_this_user_owns_into_a_private_chain_is_allowed() {
        var target = Tmp.CreateDir("target").Path;
        var link = Tmp.PathTo("link");
        DirectoryLink.Create(link, target);

        await Assert.That(AncestorChain.Violation(link)).IsNull();
    }
}
