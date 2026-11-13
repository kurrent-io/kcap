using System.Text;

namespace Capacitor.Cli;

/// <summary>Console output on Windows in UTF-16, which .NET writes with <c>WriteConsoleW</c>. Under the OEM
/// code page a console starts in, the encoder best-fits what the page lacks: "…" prints as ".", "—" as "-",
/// an emoji as "??", and "→" as a control character the console draws as an empty box. The console's own
/// code page is left alone, so the shell and the children sharing the console see no change.</summary>
static class WindowsConsoleOutput {
    public static void UseUnicode(string command) {
        if (!OperatingSystem.IsWindows()) return;

        var (console, utf8Out, utf8Error) = For(command, Console.IsOutputRedirected, Console.IsErrorRedirected);

        if (console) Console.OutputEncoding = Encoding.Unicode;
        if (utf8Out) Console.SetOut(Utf8Writer(Console.OpenStandardOutput()));
        if (utf8Error) Console.SetError(Utf8Writer(Console.OpenStandardError()));
    }

    /// <summary>UTF-16 only with both streams on the console, since they share the encoding. A redirected stream
    /// goes to an agent's shell tool, a pipe or a file, which read UTF-8. The MCP servers and the status line
    /// write UTF-8 to the raw output stream themselves.</summary>
    internal static (bool Console, bool Utf8Out, bool Utf8Error) For(string command, bool outputRedirected, bool errorRedirected) =>
        command is "mcp" or "statusline" ? default : (!outputRedirected && !errorRedirected, outputRedirected, errorRedirected);

    internal static TextWriter Utf8Writer(Stream stream) =>
        TextWriter.Synchronized(new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true });
}
