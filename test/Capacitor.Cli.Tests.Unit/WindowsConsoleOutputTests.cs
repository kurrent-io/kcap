namespace Capacitor.Cli.Tests.Unit;

public class WindowsConsoleOutputTests {
    [Test]
    public async Task A_command_writing_both_streams_to_the_console_gets_unicode() {
        await Assert.That(WindowsConsoleOutput.Applies("local", outputRedirected: false, errorRedirected: false)).IsTrue();
        await Assert.That(WindowsConsoleOutput.Applies("", outputRedirected: false, errorRedirected: false)).IsTrue();
    }

    /// <summary>A reader of a pipe or a file, such as the desktop app parsing JSON lines, keeps the bytes it
    /// always got.</summary>
    [Test]
    public async Task A_redirected_stream_keeps_the_code_page() {
        await Assert.That(WindowsConsoleOutput.Applies("local", outputRedirected: true, errorRedirected: false)).IsFalse();
        await Assert.That(WindowsConsoleOutput.Applies("local", outputRedirected: false, errorRedirected: true)).IsFalse();
        await Assert.That(WindowsConsoleOutput.Applies("setup", outputRedirected: true, errorRedirected: true)).IsFalse();
    }

    [Test]
    public async Task Commands_writing_utf8_bytes_to_the_raw_stream_keep_the_code_page() {
        await Assert.That(WindowsConsoleOutput.Applies("mcp", outputRedirected: false, errorRedirected: false)).IsFalse();
        await Assert.That(WindowsConsoleOutput.Applies("statusline", outputRedirected: false, errorRedirected: false)).IsFalse();
    }
}
