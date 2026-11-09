using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>A local identity reaches a server only once that server is known to store it: an older one would keep
/// it as an ordinary repository, link it to GitHub and ask GitHub about it.</summary>
public class LocalRepositorySupportTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly string Below = Lower(LocalRepositorySupport.MinimumServerVersion);

    [Test]
    public async Task Only_a_known_version_at_or_above_the_minimum_accepts() {
        await Assert.That(LocalRepositorySupport.Accepts(LocalRepositorySupport.MinimumServerVersion)).IsTrue();
        await Assert.That(LocalRepositorySupport.Accepts("999.0.0")).IsTrue();
        await Assert.That(LocalRepositorySupport.Accepts(Below)).IsFalse();
        await Assert.That(LocalRepositorySupport.Accepts($"{LocalRepositorySupport.MinimumServerVersion}-beta.1")).IsFalse();
        await Assert.That(LocalRepositorySupport.Accepts("unknown")).IsFalse();
        await Assert.That(LocalRepositorySupport.Accepts(null)).IsFalse();
    }

    [Test]
    public async Task A_server_is_judged_by_the_version_it_last_reported() {
        ServerVersionStore.Set("https://new.example", "999.0.0", Config.Root, TimeProvider.System);
        ServerVersionStore.Set("https://old.example", Below, Config.Root, TimeProvider.System);

        await Assert.That(LocalRepositorySupport.AcceptedBy("https://new.example", Config.Root)).IsTrue();
        await Assert.That(LocalRepositorySupport.AcceptedBy("https://old.example", Config.Root)).IsFalse();
        await Assert.That(LocalRepositorySupport.AcceptedBy("https://never-seen.example", Config.Root)).IsFalse();
        await Assert.That(LocalRepositorySupport.AcceptedBy(null, Config.Root)).IsFalse();
    }

    // Which profile a session reports to depends on where it runs, so every server it could reach must accept.
    [Test]
    [Arguments(new[] { "999.0.0" }, true)]
    [Arguments(new[] { "999.0.0", "999.1.0" }, true)]
    [Arguments(new[] { "999.0.0", "old" }, false)]
    [Arguments(new[] { "999.0.0", "unknown" }, false)]
    [Arguments(new string[0], false)]
    public async Task Every_configured_server_must_accept(string[] versions, bool expected) {
        var profiles = new Dictionary<string, Profile>();

        for (var i = 0; i < versions.Length; i++) {
            var url = $"https://server{i}.example";
            profiles[$"p{i}"] = new Profile { ServerUrl = url };
            if (versions[i] != "unknown")
                ServerVersionStore.Set(url, versions[i] == "old" ? Below : versions[i], Config.Root, TimeProvider.System);
        }

        await ConfigMutator.MutateAsync(Config.Root, c => c with { Profiles = profiles });

        await Assert.That(await LocalRepositorySupport.AcceptedEverywhereAsync(Config.Root, ProfileOverrides.None)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_url_override_must_accept_too() {
        ServerVersionStore.Set("https://server.example", "999.0.0", Config.Root, TimeProvider.System);
        await ConfigMutator.MutateAsync(Config.Root, c => c with { Profiles = new() { ["p"] = new Profile { ServerUrl = "https://server.example" } } });

        await Assert.That(await LocalRepositorySupport.AcceptedEverywhereAsync(Config.Root, new ProfileOverrides("https://override.example", null)))
            .IsFalse();
    }

    static string Lower(string version) {
        var parts = version.Split('-')[0].Split('.').Select(int.Parse).ToArray();

        return parts switch {
            [var major, var minor, > 0 and var patch] => $"{major}.{minor}.{patch - 1}",
            [var major, > 0 and var minor, _]         => $"{major}.{minor - 1}.99",
            [var major, _, _]                         => $"{major - 1}.99.99",
            _                                         => "0.0.0"
        };
    }
}
