namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Recognises what a terminal emulator writes back on its own when the agent queries it: a cursor
/// position, status, device attributes, mode, window or keyboard-flags report, a focus change, or an
/// OSC or DCS answer. A viewer's emulator forwards these as input, but they leave the composer as it
/// was, and no key a viewer presses encodes to one of these shapes.
/// </summary>
internal static class TerminalReply {
    const byte Esc = 0x1b;
    const byte Bel = 0x07;

    /// <summary>True when <paramref name="data"/> is one or more replies and nothing else.</summary>
    internal static bool IsOnlyReplies(ReadOnlySpan<byte> data) {
        if (data.IsEmpty) return false;

        while (!data.IsEmpty) {
            var length = ReplyLength(data);
            if (length == 0) return false;
            data = data[length..];
        }

        return true;
    }

    static int ReplyLength(ReadOnlySpan<byte> data) {
        if (data.Length < 3 || data[0] != Esc) return 0;

        return data[1] switch {
            (byte)'['              => CsiReplyLength(data),
            (byte)']' or (byte)'P' => StringReplyLength(data),
            _                      => 0,
        };
    }

    static int CsiReplyLength(ReadOnlySpan<byte> data) {
        if (data[2] is (byte)'I' or (byte)'O') return 3;

        var i      = 2;
        var prefix = data[i] is (byte)'?' or (byte)'>' or (byte)'=' ? data[i++] : (byte)0;
        var start  = i;
        while (i < data.Length && (char.IsAsciiDigit((char)data[i]) || data[i] == (byte)';')) i++;
        if (i == start || i >= data.Length) return 0;

        return (data[i], prefix) switch {
            ((byte)'R', 0 or (byte)'?')                            => i + 1,
            ((byte)'n', 0 or (byte)'?')                            => i + 1,
            ((byte)'t', 0)                                         => i + 1,
            ((byte)'c', (byte)'?' or (byte)'>')                    => i + 1,
            ((byte)'u', (byte)'?')                                 => i + 1,
            ((byte)'$', _) when i + 1 < data.Length && data[i + 1] == (byte)'y' => i + 2,
            _                                                      => 0,
        };
    }

    /// <summary>An OSC answer ends at BEL or ST, a DCS answer at ST.</summary>
    static int StringReplyLength(ReadOnlySpan<byte> data) {
        for (var i = 2; i < data.Length; i++) {
            if (data[i] == Bel && data[1] == (byte)']') return i + 1;
            if (data[i] == Esc && i + 1 < data.Length && data[i + 1] == (byte)'\\') return i + 2;
        }

        return 0;
    }
}
