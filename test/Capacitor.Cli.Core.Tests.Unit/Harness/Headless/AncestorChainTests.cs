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
        AncestorChain.WindowsAclViolation(entries, hasParent, Owners);

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

    /// <summary>The link resolves into a world-writable, non-sticky directory: the lexical path is all
    /// owner-only, so only walking the target's chain finds it.</summary>
    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task A_link_in_the_chain_is_checked_through_its_target() {
        var shared = Tmp.CreateDir("shared").Path;
        var target = Tmp.CreateDir("shared", "target").Path;
        File.SetUnixFileMode(shared, (UnixFileMode)0b111_111_111);
        var link = Tmp.PathTo("link");
        Directory.CreateSymbolicLink(link, target);

        var violation = AncestorChain.Violation(Path.Combine(link, "leaf"));

        await Assert.That(violation).IsNotNull();
        await Assert.That(violation!).Contains(Tmp.GetResolvedPath("shared"));
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task A_link_this_user_owns_into_a_private_chain_is_allowed() {
        var target = Tmp.CreateDir("target").Path;
        var link = Tmp.PathTo("link");
        Directory.CreateSymbolicLink(link, target);

        await Assert.That(AncestorChain.Violation(link)).IsNull();
    }
}
