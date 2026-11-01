using System.Text;

namespace Capacitor.Cli;

/// <summary>Console output on Windows in UTF-16, which .NET writes with <c>WriteConsoleW</c>. Under the OEM
/// code page a console starts in, the encoder best-fits what the page lacks: "…" prints as ".", "—" as "-",
/// and "→" as a control character the console draws as an empty box. The console's own code page is left
/// alone, so the shell and the children sharing the console see no change.</summary>
static class WindowsConsoleOutput {
    public static void UseUnicode(string command) {
        if (OperatingSystem.IsWindows() && Applies(command, Console.IsOutputRedirected, Console.IsErrorRedirected))
            Console.OutputEncoding = Encoding.Unicode;
    }

    /// <summary>Only with both streams on the console: they share the encoding, and UTF-16 reaching a file or
    /// a pipe would break a reader that expects the code page's bytes. The MCP servers and the status line
    /// write UTF-8 to the raw output stream, which a console stream in UTF-16 mode reads as UTF-16.</summary>
    internal static bool Applies(string command, bool outputRedirected, bool errorRedirected) =>
        !outputRedirected && !errorRedirected && command is not ("mcp" or "statusline");
}
