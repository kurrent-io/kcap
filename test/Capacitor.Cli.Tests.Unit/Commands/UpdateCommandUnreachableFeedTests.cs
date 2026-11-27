using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap update --check</c> against a release feed it cannot reach still answers with the version
/// an earlier check saw, and says that it is stale, in its JSON and on stderr.
/// </summary>
// Bare: the command writes to Console.
[NotInParallel]
public class UpdateCommandUnreachableFeedTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    [Arguments(true, "999.0.0", false)]
    [Arguments(false, "998.0.0", true)]
    public async Task The_check_says_when_its_answer_comes_from_an_earlier_check(
            bool reachable, string latest, bool stale) {
        var cache = UpdateCommand.CachePathFor("latest", Config.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        await File.WriteAllTextAsync(cache,
            new UpdateCommand.UpdateCacheRecord("998.0.0", DateTimeOffset.UtcNow.AddDays(-2), null, false).ToJson());

        var npm = new AuthHttpScript(_ => reachable
            ? AuthHttp.Json("""{"version":"999.0.0"}""")
            : throw new HttpRequestException("unreachable"));

        var profiles = Resolutions.Of(new Profile(), "unreachable-feed", null);
        var services = new ServiceCollection();
        services.AddSingleton(Config.Root);
        services.AddSingleton(profiles);
        services.AddSingleton(new CapacitorServer("", Config.Root, profiles));
        services.AddCapacitorHttp(ProfileOverrides.None, MachineAuth.None);
        await using var sp = services.BuildServiceProvider();

        var command = new UpdateCommand(
            Config.Root, profiles, new NpmRegistryClient(new HttpClient(npm) { BaseAddress = new Uri("http://registry.test/") }),
            sp.GetRequiredService<CapacitorServer>(), sp.GetRequiredService<ICapacitorHttpClient>(), TimeProvider.System,
            kind: InstallKind.Npm);

        using var output = ConsoleOutput.StartFullCapture();
        await command.HandleAsync(["--check"]);

        var json = JsonNode.Parse(output.GetCapturedOutput())!;

        await Assert.That(json["latest"]!.GetValue<string>()).IsEqualTo(latest);
        await Assert.That(json["stale"]!.GetValue<bool>()).IsEqualTo(stale);
        await Assert.That(output.GetCapturedError().Contains(UpdateCommand.StaleNotice)).IsEqualTo(stale);
    }
}
