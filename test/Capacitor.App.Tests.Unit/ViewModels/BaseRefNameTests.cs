using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// The launcher refuses names git's branch-name check would, so a typo never reaches a machine.
public class BaseRefNameTests {
    [Test]
    [Arguments("main")]
    [Arguments("feature/x-1")]
    [Arguments("v1.2.0")]
    [Arguments("0123abc")]
    [Arguments("origin/main")]
    public async Task A_plausible_name_is_accepted(string name) {
        await Assert.That(BaseRefName.Problem(name)).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("@")]
    [Arguments("-main")]
    [Arguments("/main")]
    [Arguments("main/")]
    [Arguments("a//b")]
    [Arguments("main.")]
    [Arguments("a..b")]
    [Arguments("a@{1}")]
    [Arguments("a b")]
    [Arguments("a~1")]
    [Arguments("a^")]
    [Arguments("a:b")]
    [Arguments("a?")]
    [Arguments("a*")]
    [Arguments("a[b")]
    [Arguments("a\\b")]
    [Arguments(".hidden")]
    [Arguments("a/.b")]
    [Arguments("a.lock")]
    public async Task A_name_git_rejects_is_refused(string name) {
        await Assert.That(BaseRefName.Problem(name)).IsNotNull();
    }
}
