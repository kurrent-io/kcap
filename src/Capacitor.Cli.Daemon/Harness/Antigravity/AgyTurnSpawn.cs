using System.Diagnostics;

namespace Capacitor.Cli.Daemon.Harness.Antigravity;

/// <summary>
/// One <c>agy</c> turn to start: the command, and the prompt it is handed on stdin. They travel apart
/// because the prompt must never be part of the command. A class rather than a record so a log line
/// that formats one cannot print the prompt.
/// </summary>
internal sealed class AgyTurnSpawn(ProcessStartInfo psi, string prompt) {
    public ProcessStartInfo Psi    => psi;
    public string           Prompt => prompt;
}
