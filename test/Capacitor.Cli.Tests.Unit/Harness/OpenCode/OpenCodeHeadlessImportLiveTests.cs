using System.Collections;
using System.Security.Cryptography;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Headless;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Harness.OpenCode;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Tests.Unit.Harness.OpenCode;

/// <summary>
/// A headless OpenCode run against the real binary, spending one model call, so it runs only with
/// <see cref="LiveGateEnvVar"/>=1, <c>opencode</c> on PATH and <c>ANTHROPIC_API_KEY</c> set. A temporary
/// <c>XDG_DATA_HOME</c> holding a seeded <c>opencode.db</c> stands in for the user's.
/// </summary>
[NotInParallel]
public class OpenCodeHeadlessImportLiveTests {
    const string LiveGateEnvVar = "KCAP_OPENCODE_LIVE";

    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>The wrapper lists the run's own data directory once OpenCode exits: the session went somewhere,
    /// so its absence from the user's database means something.</summary>
    [Test]
    public async Task The_runs_session_never_reaches_the_users_opencode_db() {
        Skip.Unless(Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Spends a real model call: set {LiveGateEnvVar}=1 with `opencode` on PATH and ANTHROPIC_API_KEY set to run");
        Skip.When(OperatingSystem.IsWindows(), "the wrapper is a POSIX shell script");
        Skip.When(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")), "ANTHROPIC_API_KEY is not set");
        var opencode = BinaryProbe.FromEnvironment().Resolve("opencode");
        Skip.When(opencode is null, "opencode is not on PATH");

        var userConfig = Tmp.CreateDir("user-config").Path;
        var userData   = Tmp.CreateDir("user-data").Path;
        var userDb     = Path.Combine(userData, "opencode", "opencode.db");
        Directory.CreateDirectory(Path.GetDirectoryName(userDb)!);

        using (var seeded = new OpenCodeDbFixture()) {
            seeded.AddSession("ses_users_own", null, "/work/mine", "Mine", 1782241513759);
            seeded.AddMessageWithText("ses_users_own", "msg_1", "hello", 1782241513760);
            SqliteConnection.ClearAllPools();
            File.Copy(seeded.DbPath, userDb);
        }

        var before  = SHA256.HashData(await File.ReadAllBytesAsync(userDb));
        var privateListing = Path.Combine(Tmp.Path, "private-data");
        var wrapper = Tmp.CreateExecutable("bin/opencode",
            $"#!/bin/sh\n'{opencode}' \"$@\"\nstatus=$?\nls -A \"$XDG_DATA_HOME/opencode\" > '{privateListing}'\nexit $status\n");

        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string?)e.Value;
        foreach (var name in new[] { "OPENCODE_CONFIG", "OPENCODE_CONFIG_DIR", "OPENCODE_CONFIG_CONTENT", "OPENCODE_PERMISSION", "OPENCODE_AUTH_CONTENT" })
            env.Remove(name);
        env["XDG_CONFIG_HOME"] = userConfig;
        env["XDG_DATA_HOME"]   = userData;

        var outcome = await OpenCodeCliRunner.RunDetailedAsync("Reply with the single word: ok", TimeSpan.FromSeconds(120),
            TimeProvider.System, Console.WriteLine, new HeadlessRunRoot(Tmp.CreateDir("state").Path, "live"),
            new OpenCodePaths(new(Tmp.Path), null, userConfig, userData), env, "anthropic/claude-haiku-4-5", null, null, wrapper,
            CancellationToken.None);

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(await File.ReadAllLinesAsync(privateListing)).Contains("opencode.db");
        await Assert.That(SHA256.HashData(await File.ReadAllBytesAsync(userDb))).IsEquivalentTo(before);

        var discovered = await new OpenCodeImportSource(userDb, Path.Combine(Tmp.Path, "ledger.json"), TimeProvider.System)
            .DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(discovered.Select(s => s.SessionId)).IsEquivalentTo(["ses_users_own"]);
    }
}
