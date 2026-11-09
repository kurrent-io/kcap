using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalRootAccessTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly Func<string, IReadOnlyList<string>> NoOthers = _ => [];

    static void CannotSecure(string _) => throw new UnauthorizedAccessException("access is denied");

    [Test]
    public async Task An_owner_only_root_is_left_alone() {
        string dir = Tmp.CreateDir("local");
        var secured = false;

        LocalRootAccess.Secure(dir, installed: true, _ => null, NoOthers, _ => secured = true, TextWriter.Null);

        await Assert.That(secured).IsFalse();
    }

    /// <summary>Nothing is lost by choosing another root yet, and one other accounts can read would hold every
    /// session.</summary>
    [Test]
    public async Task A_root_that_cannot_be_secured_is_refused_while_it_holds_no_installation() {
        string dir = Tmp.CreateDir("local");

        var ex = Assert.Throws<LocalLifecycleException>(() =>
            LocalRootAccess.Secure(dir, installed: false, _ => "its DACL grants S-1-5-32-545", NoOthers, CannotSecure, TextWriter.Null));

        await Assert.That(ex!.Message).Contains(LocalRoot.EnvVar);
    }

    [Test]
    public async Task A_missing_root_that_cannot_be_created_owner_only_is_refused() {
        var path = Tmp.PathTo("local");

        await Assert.That(() => LocalRootAccess.Secure(path, installed: false, _ => null, NoOthers, CannotSecure, TextWriter.Null))
            .Throws<LocalLifecycleException>();
    }

    /// <summary>Refusing would brick an installation whose root sits where no ACL can be set.</summary>
    [Test]
    public async Task An_installation_whose_root_cannot_be_secured_starts_with_a_warning() {
        string dir = Tmp.CreateDir("local");
        var warnings = new StringWriter();

        LocalRootAccess.Secure(dir, installed: true, _ => "its DACL inherits from its parent", NoOthers, CannotSecure, warnings);

        await Assert.That(warnings.ToString()).Contains("could not make");
        await Assert.That(warnings.ToString()).Contains(dir);
    }

    [Test]
    public async Task Tightening_an_installation_open_to_others_names_them() {
        string dir = Tmp.CreateDir("local");
        var warnings = new StringWriter();
        var secured = false;

        LocalRootAccess.Secure(dir, installed: true, _ => "its DACL grants S-1-5-32-545", _ => ["BUILTIN\\Users"], _ => secured = true, warnings);

        await Assert.That(secured).IsTrue();
        await Assert.That(warnings.ToString()).Contains("BUILTIN\\Users");
    }

    [Test]
    public async Task Tightening_a_root_that_holds_no_installation_is_silent() {
        string dir = Tmp.CreateDir("local");
        var warnings = new StringWriter();
        var secured = false;

        LocalRootAccess.Secure(dir, installed: false, _ => "its DACL inherits from its parent", _ => ["BUILTIN\\Users"], _ => secured = true, warnings);

        await Assert.That(secured).IsTrue();
        await Assert.That(warnings.ToString()).IsEmpty();
    }
}
