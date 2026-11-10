using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording;

/// <summary>Places a transcript row against the input that produced it.</summary>
public interface IRowStamper {
    /// <summary>Called for a raw line each time it is about to be delivered, so a redelivered line is asked about
    /// again.</summary>
    LineStamp? StampFor(int lineNumber, string rawLine);

    /// <summary>The server or the spool now holds every line through <paramref name="throughLine"/>.</summary>
    void Acknowledged(int throughLine);
}
