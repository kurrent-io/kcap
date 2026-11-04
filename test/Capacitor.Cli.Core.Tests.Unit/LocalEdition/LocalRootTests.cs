using System.Runtime.Versioning;
using System.Security.AccessControl;
using Capacitor.Cli.Core.LocalEdition;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalRootTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Defaults_to_a_fixed_directory_under_home() {
        var root = LocalRoot.Resolve(null, new UserHome("/home/me"));

        await Assert.That(root.Directory).IsEqualTo(Path.Combine("/home/me", ".config", "kcap", "local"));
    }

    /// <summary>The server trims and absolutizes the override the same way; a difference would put the
    /// CLI's journal somewhere the server does not look.</summary>
    [Test]
    public async Task An_override_is_absolutized_and_trimmed_as_the_server_does() {
        var configured = Tmp.PathTo("stack") + Path.DirectorySeparatorChar;

        var root = LocalRoot.Resolve(configured, new UserHome("/home/me"));

        await Assert.That(root.Directory).IsEqualTo(Tmp.PathTo("stack"));
    }

    [Test]
    public async Task EnsureCreated_makes_the_root_owner_only() {
        if (OperatingSystem.IsWindows()) return;

        var root = new LocalRoot(Tmp.CreateDir("local"));
        File.SetUnixFileMode(root.Directory, (UnixFileMode)0b111_101_101);

        root.EnsureCreated();

        await Assert.That(File.GetUnixFileMode(root.Directory))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>The loose mode and the registry beside it would make Windows warn; Unix tightens without a word.</summary>
    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task EnsureCreated_on_Unix_tightens_an_installation_without_a_warning() {
        var root = new LocalRoot(Tmp.CreateDir("local"));
        File.SetUnixFileMode(root.Directory, (UnixFileMode)0b111_111_111);
        Tmp.CreateFile(["local", "install.json"], "{}");
        var warnings = new StringWriter();

        root.EnsureCreated(warnings: warnings);

        await Assert.That(File.GetUnixFileMode(root.Directory))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Assert.That(warnings.ToString()).IsEmpty();
    }

    /// <summary>The Users grant on the parent is what the root would otherwise inherit.</summary>
    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task EnsureCreated_gives_a_new_root_a_protected_dacl_for_the_user_and_SYSTEM_alone() {
        string parent = Tmp.CreateDir("parent");
        WindowsAcl.GrantUsersRead(parent);
        var root = new LocalRoot(Tmp.PathTo("parent", "local"));
        var warnings = new StringWriter();

        root.EnsureCreated(warnings: warnings);

        await Assert.That(WindowsAcl.IsProtected(root.Directory)).IsTrue();
        await Assert.That(WindowsAcl.Identities(root.Directory)).IsEquivalentTo(WindowsAcl.OwnerOnly);

        foreach (var rule in WindowsAcl.Rules(root.Directory)) {
            await Assert.That(rule.AccessControlType).IsEqualTo(AccessControlType.Allow);
            await Assert.That(rule.FileSystemRights.HasFlag(FileSystemRights.FullControl)).IsTrue();
            await Assert.That(rule.InheritanceFlags).IsEqualTo(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit);
            await Assert.That(rule.IsInherited).IsFalse();
        }

        await Assert.That(warnings.ToString()).IsEmpty();
    }

    /// <summary>The database file stands for what the server already wrote: it loses the Users grant through
    /// inheritance alone. A second start finds nothing to tighten and says nothing.</summary>
    [Test, RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task EnsureCreated_tightens_an_installation_open_to_Users_and_warns_instead_of_failing() {
        string dir = Tmp.CreateDir("local");
        WindowsAcl.GrantUsersRead(dir);
        Tmp.CreateFile(["local", "install.json"], "{}");
        var database = Tmp.CreateFile(["local", "data", "pg", "base.dat"], "rows");
        await Assert.That(WindowsAcl.Identities(database)).Contains(WindowsAcl.Users.Value);
        var root = new LocalRoot(dir);
        var warnings = new StringWriter();

        root.EnsureCreated(warnings: warnings);

        await Assert.That(WindowsAcl.IsProtected(dir)).IsTrue();
        await Assert.That(WindowsAcl.Identities(dir)).IsEquivalentTo(WindowsAcl.OwnerOnly);
        await Assert.That(WindowsAcl.Identities(database)).IsEquivalentTo(WindowsAcl.OwnerOnly);
        await Assert.That(warnings.ToString()).Contains("now private to you");

        var again = new StringWriter();
        root.EnsureCreated(warnings: again);

        await Assert.That(again.ToString()).IsEmpty();
    }

    [Test]
    public async Task EnsureCreated_refuses_a_symlinked_root() {
        var target = Tmp.CreateDir("elsewhere").Path;
        var link   = Tmp.PathTo("local");
        Directory.CreateSymbolicLink(link, target);

        await Assert.That(() => new LocalRoot(link).EnsureCreated()).Throws<LocalLifecycleException>();
    }
}
