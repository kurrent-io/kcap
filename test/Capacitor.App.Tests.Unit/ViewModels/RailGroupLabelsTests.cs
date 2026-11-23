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
    public async Task Groups_whose_qualifiers_also_match_keep_the_plain_label() {
        var shown = RailGroupLabels.Disambiguate([("daemon:a:/src/demo", "demo"), ("daemon:b:/src/demo", "demo")]);
        await Assert.That(shown["daemon:a:/src/demo"]).IsEqualTo("demo");
        await Assert.That(shown["daemon:b:/src/demo"]).IsEqualTo("demo");
    }
}
