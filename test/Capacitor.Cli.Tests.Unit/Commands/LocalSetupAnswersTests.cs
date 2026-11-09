using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The answers a local setup keeps for its next unattended run: bound to one installation, and none whenever
/// the file cannot say, so an install that predates the file keeps the defaults it had.</summary>
public class LocalSetupAnswersTests {
    const string Installation = "0123456789abcdef0123456789abcdef";

    [TempDir] public required TempDir Tmp { get; init; }

    ConfigRoot Config => field ??= new(Tmp.PathTo("config"));

    [Test]
    public async Task Answers_round_trip_for_their_installation() {
        var saved = new LocalSetupAnswers(Installation, Harnesses: false,
            LeftOut: new ImportSkip([("acme", "secret")], [Path.Combine("work", "secret-project")]), Visibility: "project");

        await Assert.That(saved.Save(Config)).IsTrue();

        var read = LocalSetupAnswers.For(Config, Installation);
        await Assert.That(read).IsEqualTo(saved);
        await Assert.That(read.LeftOut!.Covers((null, null), Path.Combine("work", "secret-project"))).IsTrue();
    }

    [Test]
    public async Task Another_installation_reads_as_none() {
        new LocalSetupAnswers(Installation, Harnesses: false).Save(Config);

        await Assert.That(LocalSetupAnswers.For(Config, "fedcba9876543210fedcba9876543210"))
            .IsEqualTo(new LocalSetupAnswers("fedcba9876543210fedcba9876543210"));
    }

    /// <summary>Setup uses a remembered visibility without checking it again, so one it would refuse as a flag reads as none
    /// while the other answers stand.</summary>
    [Test]
    public async Task An_unknown_visibility_reads_as_none() {
        Directory.CreateDirectory(Config.Directory);
        File.WriteAllText(Config.Path(LocalSetupAnswers.FileName),
            $$"""{"installation_id":"{{Installation}}","harnesses":false,"visibility":"everyone"}""");

        await Assert.That(LocalSetupAnswers.For(Config, Installation)).IsEqualTo(new LocalSetupAnswers(Installation, Harnesses: false));
    }

    [Test]
    [Arguments(null)]
    [Arguments("not json")]
    [Arguments("""{"installation_id":"0123456789abcdef0123456789abcdef","harnesses":"maybe"}""")]
    public async Task A_missing_or_unreadable_file_reads_as_none(string? content) {
        if (content is not null) {
            Directory.CreateDirectory(Config.Directory);
            File.WriteAllText(Config.Path(LocalSetupAnswers.FileName), content);
        }

        await Assert.That(LocalSetupAnswers.For(Config, Installation)).IsEqualTo(new LocalSetupAnswers(Installation));
    }
}
