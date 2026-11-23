using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class RailGroupLabelsTests {
    [Test]
    public async Task Unique_labels_stay_as_they_are() {
        var shown = RailGroupLabels.Disambiguate([("repo:acme/api", "acme/api"), ("path:/src/demo", "demo")]);
        await Assert.That(shown["repo:acme/api"]).IsEqualTo("acme/api");
        await Assert.That(shown["path:/src/demo"]).IsEqualTo("demo");
    }

    [Test]
    public async Task Two_folders_with_the_same_name_show_their_parent_folders() {
        var shown = RailGroupLabels.Disambiguate([("path:/Users/me/work/demo", "demo"), ("path:/Users/me/play/demo", "demo")]);
        await Assert.That(shown["path:/Users/me/work/demo"]).IsEqualTo("work/demo");
        await Assert.That(shown["path:/Users/me/play/demo"]).IsEqualTo("play/demo");
    }

    [Test]
    public async Task A_remote_daemon_folder_and_a_repository_with_the_same_name_are_told_apart() {
        var shown = RailGroupLabels.Disambiguate([("daemon:box:/home/ci/demo", "demo"), ("repo:acme/demo", "demo")]);
        await Assert.That(shown["daemon:box:/home/ci/demo"]).IsEqualTo("ci/demo");
        await Assert.That(shown["repo:acme/demo"]).IsEqualTo("acme/demo");
    }

    [Test]
    public async Task A_longer_path_distinguishes_folders_with_the_same_parent_name() {
        var shown = RailGroupLabels.Disambiguate([("path:/src/team/demo", "demo"), ("path:/work/team/demo", "demo")]);
        await Assert.That(shown["path:/src/team/demo"]).IsEqualTo("src/team/demo");
        await Assert.That(shown["path:/work/team/demo"]).IsEqualTo("work/team/demo");
    }

    [Test]
    public async Task A_generated_label_cannot_take_a_unique_repository_label() {
        var shown = RailGroupLabels.Disambiguate([
            ("path:/Users/me/acme/demo", "demo"), ("path:/Users/me/work/demo", "demo"), ("repo:acme/demo", "Acme/Demo"),
        ]);
        await Assert.That(shown["path:/Users/me/acme/demo"]).IsEqualTo("me/acme/demo");
        await Assert.That(shown["path:/Users/me/work/demo"]).IsEqualTo("work/demo");
        await Assert.That(shown["repo:acme/demo"]).IsEqualTo("Acme/Demo");
    }

    [Test]
    public async Task Identical_paths_on_different_daemons_show_the_daemon() {
        var shown = RailGroupLabels.Disambiguate([("daemon:owner/a:/src/demo", "demo"), ("daemon:owner/b:/src/demo", "demo")]);
        await Assert.That(shown["daemon:owner/a:/src/demo"]).IsEqualTo("a/src/demo");
        await Assert.That(shown["daemon:owner/b:/src/demo"]).IsEqualTo("b/src/demo");
    }
}
