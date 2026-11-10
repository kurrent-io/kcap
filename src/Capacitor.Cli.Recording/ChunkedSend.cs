namespace Capacitor.Cli.Recording;

/// <param name="AckedNextLine">The line the server's last acknowledgement resumes at; null when the sends carry none.</param>
/// <param name="Complete">Whether every chunk was sent and acknowledged through its last line. Only then has the server
/// taken the commits the batch carried.</param>
public readonly record struct ChunkedSend(int? AckedNextLine, bool Complete);
