using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.App.Services;

/// The emulator answers the program's own queries (cursor position, device attributes, focus, OSC colours) through
/// its input. Typed into a remote PTY they arrive as keystrokes and can pick a prompt's default, and they come glued
/// to real input, so they are stripped rather than matched. The web viewer strips the same set.
public static partial class TerminalReportFilter {
    [GeneratedRegex(@"\x1b\[[?>]?[0-9;]*[Rcn]|\x1b\[[IO]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)")]
    private static partial Regex Reports();

    public static byte[] Strip(byte[] input) {
        if (Array.IndexOf(input, (byte)0x1b) < 0) return input;
        // Latin-1 maps each byte to one char and back, so UTF-8 sequences outside the matches survive intact.
        return Encoding.Latin1.GetBytes(Reports().Replace(Encoding.Latin1.GetString(input), ""));
    }
}
