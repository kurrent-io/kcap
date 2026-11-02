using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

/// <summary>
/// The short-circuits that skip repo discovery, and what they must not skip.
/// </summary>
public class ResolveForRepoTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir]        public required TempDir        Repo   { get; init; }

    const string ActiveUrl = "https://active.example";
    const string PinnedUrl = "https://pinned.example";

    /// A repo pinned to a profile that is NOT the active one, so a resolution that skipped repo
    /// discovery answers a different server than one that did.
    async Task WriteRepoPinnedToAnotherProfile() {
        await ConfigMutator.MutateAsync(Config.Root, _ => new ProfileConfig {
            ActiveProfile = "active",
            Profiles = new() {
                ["active"] = new Profile { ServerUrl = ActiveUrl },
                ["pinned"] = new Profile { ServerUrl = PinnedUrl }
            }
        });
        Repo.CreateFile(".kcap.json", """{"profile":"pinned"}""");
    }

    async Task<string?> ResolveIn(string[] args, ProfileOverrides env) =>
        (await AppConfig.ResolveForRepo(args, Config.Root, env, NoLocalInstallation.Endpoints, new WorkingDirectory(Repo.Path), gitTimeoutMs: 1000))
        .Resolution.ServerUrl;

    [Test]
    public async Task Nothing_overridden_resolves_the_repos_own_profile() {
        await WriteRepoPinnedToAnotherProfile();

        await Assert.That(await ResolveIn([], ProfileOverrides.None)).IsEqualTo(PinnedUrl);
    }

    /// <summary>A named override outranks what the repo pins, so the profile named in
    /// <c>.kcap.json</c> loses to it. Says nothing about whether discovery ran — both paths feed the
    /// resolver the same override, and only the git probe tells them apart.</summary>
    [Test]
    public async Task A_named_override_outranks_the_repos_own_profile() {
        await WriteRepoPinnedToAnotherProfile();

        await Assert.That(await ResolveIn([], new ProfileOverrides("https://env.example", null)))
                    .IsEqualTo("https://env.example");
    }

    [Test]
    public async Task A_named_profile_outranks_the_repos_own_profile() {
        await WriteRepoPinnedToAnotherProfile();

        await Assert.That(await ResolveIn([], new ProfileOverrides(null, "active"))).IsEqualTo(ActiveUrl);
    }

    /// <summary>The remote probe is skipped when no profile lists remotes, so a profile that does must
    /// still be matched by the repository's remote.</summary>
    [Test]
    public async Task A_profile_listing_the_repos_remote_is_matched() {
        await ConfigMutator.MutateAsync(Config.Root, _ => new ProfileConfig {
            ActiveProfile = "active",
            Profiles = new() {
                ["active"] = new Profile { ServerUrl = ActiveUrl },
                ["team"]   = new Profile { ServerUrl = PinnedUrl, Remotes = ["github.com/acme/*"] }
            }
        });
        using var repo = GitRepo.Create();
        repo.AddRemote("https://github.com/acme/widgets.git");

        var resolved = await AppConfig.ResolveForRepo(
            [], Config.Root, ProfileOverrides.None, NoLocalInstallation.Endpoints, new WorkingDirectory(repo.Path), gitTimeoutMs: 5000);

        await Assert.That(resolved.Resolution.ServerUrl).IsEqualTo(PinnedUrl);
    }

    /// <summary>An empty <c>--server-url</c> names no server: the resolver discards it and falls
    /// through, so withholding the repo inputs from that fall-through would silently answer the
    /// active profile instead of the one the repo pins.</summary>
    [Test]
    public async Task An_empty_server_url_flag_still_resolves_the_repos_profile() {
        await WriteRepoPinnedToAnotherProfile();

        await Assert.That(await ResolveIn(["--server-url", ""], ProfileOverrides.None)).IsEqualTo(PinnedUrl);
    }

    async Task WriteTwoProfiles(Dictionary<string, string>? bindings = null) =>
        await ConfigMutator.MutateAsync(Config.Root, _ => new ProfileConfig {
            ActiveProfile   = "active",
            Profiles        = new() {
                ["active"] = new Profile { ServerUrl = ActiveUrl },
                ["pinned"] = new Profile { ServerUrl = PinnedUrl }
            },
            ProfileBindings = bindings ?? new()
        });

    /// <summary>The root git reports places <c>.kcap.json</c>, so a file at the repository root still
    /// pins a session started in a subdirectory.</summary>
    [Test]
    public async Task A_repo_config_at_the_root_pins_a_session_in_a_subdirectory() {
        await WriteTwoProfiles();
        using var repo = GitRepo.Create();
        repo.CreateFile(".kcap.json", """{"profile":"pinned"}""");
        var deep = repo.CreateDir("src", "deep");

        var resolved = await AppConfig.ResolveForRepo(
            [], Config.Root, ProfileOverrides.None, NoLocalInstallation.Endpoints, new WorkingDirectory(deep.Path), gitTimeoutMs: 5000);

        await Assert.That(resolved.Resolution.ServerUrl).IsEqualTo(PinnedUrl);
    }

    /// <summary>A <c>kcap use</c> binding is keyed by git's own spelling of the root, so it must still
    /// be found from a subdirectory with no <c>.kcap.json</c> anywhere.</summary>
    [Test]
    public async Task A_use_binding_is_found_from_a_subdirectory() {
        using var repo = GitRepo.Create();
        await WriteTwoProfiles(new() { [repo.Do("rev-parse", "--show-toplevel").Text] = "pinned" });
        var deep = repo.CreateDir("src");

        var resolved = await AppConfig.ResolveForRepo(
            [], Config.Root, ProfileOverrides.None, NoLocalInstallation.Endpoints, new WorkingDirectory(deep.Path), gitTimeoutMs: 5000);

        await Assert.That(resolved.Resolution.ServerUrl).IsEqualTo(PinnedUrl);
    }

    [Test]
    public async Task A_repo_config_is_seen_from_any_directory_below_it() {
        var deep = Repo.CreateDir("a", "b");
        await Assert.That(AppConfig.RepoConfigAtOrAbove(deep.Path)).IsFalse();

        Repo.CreateFile(".kcap.json", "{}");

        await Assert.That(AppConfig.RepoConfigAtOrAbove(deep.Path)).IsTrue();
    }
}
