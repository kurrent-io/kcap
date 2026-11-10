using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording;

/// <summary>Stamps nothing: a recorder with no input log to place rows from.</summary>
public sealed class NoStamps : IRowStamper {
    public LineStamp? StampFor(int lineNumber, string rawLine) => null;

    public void Acknowledged(int throughLine) { }
}
