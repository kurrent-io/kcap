using Capacitor.Cli.Core;
using Capacitor.Cli.Recording;
using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Places a hosted session's rows against the messages its <see cref="InputLog"/> holds. It reads the raw line,
/// not the redacted one the batch carries, because each entry is a hash of what was pasted</summary>
internal sealed class InputLogStamper(InputLog log, PersonRows rows) : IRowStamper {
    public LineStamp? StampFor(int lineNumber, string rawLine) =>
        log.Match(lineNumber, rawLine, log.Compares(lineNumber) ? rows.Text(rawLine, lineNumber) : null);

    public void Acknowledged(int throughLine) => log.Acknowledge(throughLine);
}
