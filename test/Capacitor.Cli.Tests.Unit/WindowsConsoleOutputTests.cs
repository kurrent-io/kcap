namespace Capacitor.Cli.Tests.Unit;

public class WindowsConsoleOutputTests {
    [Test]
    public async Task A_command_writing_both_streams_to_the_console_gets_unicode() {
        await Assert.That(WindowsConsoleOutput.For("local", outputRedirected: false, errorRedirected: false)).IsEqualTo((true, false, false));
        await Assert.That(WindowsConsoleOutput.For("", outputRedirected: false, errorRedirected: false)).IsEqualTo((true, false, false));
    }

    /// <summary>An agent's shell tool, a pipe or a file reads UTF-8: under the console's code page an emoji left the
    /// CLI as "??" and a byte order mark as "?". A stream still on the console keeps the code page, since the two
    /// console streams share one encoding.</summary>
    [Test]
    public async Task A_redirected_stream_gets_utf8() {
        await Assert.That(WindowsConsoleOutput.For("recap", outputRedirected: true, errorRedirected: false)).IsEqualTo((false, true, false));
        await Assert.That(WindowsConsoleOutput.For("recap", outputRedirected: false, errorRedirected: true)).IsEqualTo((false, false, true));
        await Assert.That(WindowsConsoleOutput.For("recap", outputRedirected: true, errorRedirected: true)).IsEqualTo((false, true, true));
    }

    [Test]
    public async Task Commands_writing_utf8_bytes_to_the_raw_stream_are_left_alone() {
        await Assert.That(WindowsConsoleOutput.For("mcp", outputRedirected: false, errorRedirected: false)).IsEqualTo((false, false, false));
        await Assert.That(WindowsConsoleOutput.For("statusline", outputRedirected: true, errorRedirected: true)).IsEqualTo((false, false, false));
    }

    [Test]
    public async Task The_redirected_writer_writes_utf8_without_a_preamble() {
        using var stream = new MemoryStream();

        var writer = WindowsConsoleOutput.Utf8Writer(stream);
        writer.Write("é😀");

        await Assert.That(stream.ToArray()).IsEquivalentTo(new byte[] { 0xC3, 0xA9, 0xF0, 0x9F, 0x98, 0x80 });
    }
}
