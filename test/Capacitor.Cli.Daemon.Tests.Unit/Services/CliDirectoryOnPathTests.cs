using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class CliDirectoryOnPathTests {
    const string Cli = @"C:\Users\someone\AppData\Local\kcap\current\bin";

    /// Last, so a kcap.exe the user put on the path themselves is still the one a hook starts.
    [Test]
    public async Task A_path_that_does_not_search_the_cli_directory_gets_it_last() {
        var path = CliDirectoryOnPath.Appended(@"C:\Windows\system32;C:\tools", Cli);

        await Assert.That(path).IsEqualTo($@"C:\Windows\system32;C:\tools;{Cli}");
    }

    [Test]
    [Arguments(@"C:\Windows\system32;C:\Users\someone\AppData\Local\kcap\current\bin;C:\tools")]
    [Arguments(@"C:\Windows\system32;c:\users\someone\appdata\local\KCAP\current\bin\")]
    [Arguments(@"C:\Windows\system32; C:\Users\someone\AppData\Local\kcap\current\bin ;")]
    public async Task A_path_that_already_searches_the_cli_directory_is_left_alone(string path) {
        await Assert.That(CliDirectoryOnPath.Appended(path, Cli)).IsNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(";")]
    public async Task A_missing_path_becomes_the_cli_directory(string? path) {
        await Assert.That(CliDirectoryOnPath.Appended(path, Cli)).IsEqualTo(Cli);
    }

    [Test]
    public async Task A_separator_the_path_ends_with_is_not_doubled() {
        await Assert.That(CliDirectoryOnPath.Appended(@"C:\tools;", Cli)).IsEqualTo($@"C:\tools;{Cli}");
    }
}
