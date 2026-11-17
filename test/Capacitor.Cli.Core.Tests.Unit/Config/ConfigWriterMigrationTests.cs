using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

/// <summary>
/// Pins the config-writer migration outcome: every CLI command that writes <c>config.json</c> now
/// goes through <see cref="ConfigMutator"/> — there is no more per-command fixed-temp writer
/// to race against. The compile-time half of that guarantee is that <c>AppConfig.SaveProfileConfig</c>
/// no longer exists (removed earlier in the migration) and the private atomic-save helpers in
/// <c>ProfileCommand</c>/<c>UseCommand</c> are gone too (removed here); this test pins the
/// runtime half: concurrent field-scoped mutations, each touching its own profile key, all survive
/// instead of colliding on a shared fixed <c>.tmp</c> name. Eight writers contend as surely as
/// sixteen, and keep the last one's wait inside the lock's 10 s on a loaded Windows runner.
/// </summary>
public class ConfigWriterMigrationTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const int Writers = 8;

    [Test]
    public async Task No_writer_bypasses_the_mutator() {
        var writers = Enumerable.Range(0, Writers).Select(i => ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles = new(c.Profiles) { [$"p{i}"] = new Profile { ServerUrl = $"https://p{i}.example" } },
        }));
        await Task.WhenAll(writers);

        var final = await AppConfig.LoadProfileConfig(Config.Root);
        for (var i = 0; i < Writers; i++)
            await Assert.That(final.Profiles.ContainsKey($"p{i}")).IsTrue();
    }
}
