namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>How a headless harness process ended. <see cref="ExitCode"/> is null when it was killed: at the
/// deadline (<see cref="TimedOut"/>) or because a stderr line asked for it (<see cref="KilledEarly"/>).
/// <see cref="Stderr"/> is the tail of what it wrote there.</summary>
sealed record HeadlessProcessExit(int? ExitCode, bool TimedOut, bool KilledEarly, string Stderr);
