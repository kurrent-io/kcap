namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="RepoScope"/> — the arithmetic every caller holding an
/// <c>owner/repo</c> key shares, so the hook path, the import gate and the daemon cannot decide
/// the same lists three ways.
/// </summary>
public class RepoScopeTests {
    [Test]
    public async Task No_list_admits_everything() {
        await Assert.That(RepoScope.IsOutOfScope("acme/widgets", null, null)).IsFalse();
        await Assert.That(RepoScope.IsOutOfScope("acme/widgets", [], [])).IsFalse();
        await Assert.That(RepoScope.IsOutOfScope(null, [], [])).IsFalse();
    }

    [Test]
    public async Task An_allowlist_admits_only_what_it_names() {
        await Assert.That(RepoScope.IsOutOfScope("acme/widgets", ["acme/widgets"], null)).IsFalse();
        await Assert.That(RepoScope.IsOutOfScope("acme/secrets", ["acme/widgets"], null)).IsTrue();
    }

    // The denylist subtracts within the allowlist rather than being consulted instead of it.
    [Test]
    public async Task A_denylist_subtracts_within_an_allowlist() {
        await Assert.That(RepoScope.IsOutOfScope(
            "acme/secrets", ["acme/widgets", "acme/secrets"], ["acme/secrets"])).IsTrue();
        await Assert.That(RepoScope.IsOutOfScope(
            "acme/widgets", ["acme/widgets", "acme/secrets"], ["acme/secrets"])).IsFalse();
    }

    [Test]
    public async Task A_denylist_alone_refuses_only_what_it_names() {
        await Assert.That(RepoScope.IsOutOfScope("acme/secrets", null, ["acme/secrets"])).IsTrue();
        await Assert.That(RepoScope.IsOutOfScope("acme/widgets", null, ["acme/secrets"])).IsFalse();
    }

    // Repo keys come from remote URLs, whose casing is not meaningful.
    [Test]
    public async Task Keys_compare_case_insensitively() {
        await Assert.That(RepoScope.IsOutOfScope("ACME/Widgets", ["acme/widgets"], null)).IsFalse();
        await Assert.That(RepoScope.IsOutOfScope("ACME/Secrets", null, ["acme/secrets"])).IsTrue();
    }

    // The fail-closed direction, and the reason the two lists cannot share one rule: an allowlist
    // that admitted a repo it could not identify would restrict nothing, while a denylist has no
    // grounds to refuse one.
    [Test]
    public async Task An_unresolved_key_is_outside_an_allowlist_and_inside_no_denylist() {
        await Assert.That(RepoScope.IsOutOfScope(null, ["acme/widgets"], null)).IsTrue();
        await Assert.That(RepoScope.IsOutOfScope(null, null, ["acme/secrets"])).IsFalse();
        await Assert.That(RepoScope.IsOutsideAllowlist(null, ["acme/widgets"])).IsTrue();
        await Assert.That(RepoScope.IsOutsideAllowlist(null, null)).IsFalse();
    }

    const string LocalKey = "local~0123456789abcdef0123/demo";

    [Test]
    public async Task A_local_repository_is_named_exactly_or_by_its_folder() {
        await Assert.That(RepoScope.IsOutOfScope(LocalKey, [LocalKey], null)).IsFalse();
        await Assert.That(RepoScope.IsOutOfScope(LocalKey, ["local~/demo"], null)).IsFalse();
        await Assert.That(RepoScope.IsOutOfScope(LocalKey, null, ["local~/demo"])).IsTrue();
        await Assert.That(RepoScope.IsOutOfScope(LocalKey, null, ["local~/DEMO"])).IsTrue();
    }

    // Its folder name admits it to nothing else: a remote called the same is a different repository.
    [Test]
    public async Task A_folder_entry_names_only_local_repositories() {
        await Assert.That(RepoScope.IsOutOfScope("acme/demo", ["local~/demo"], null)).IsTrue();
        await Assert.That(RepoScope.IsOutOfScope("acme/demo", null, ["local~/demo"])).IsFalse();
        await Assert.That(RepoScope.IsOutOfScope(LocalKey, ["local/demo"], null)).IsTrue();
        await Assert.That(RepoScope.IsOutOfScope(LocalKey, ["acme/demo"], null)).IsTrue();
    }

    [Test]
    public async Task An_allowlist_of_remotes_leaves_a_local_repository_out() =>
        await Assert.That(RepoScope.IsOutOfScope(LocalKey, ["acme/widgets"], null)).IsTrue();
}
