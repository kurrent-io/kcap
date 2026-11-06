namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>A headless run's private directory could not be created or verified, so the run must not start.</summary>
public sealed class HeadlessRunRefusedException(string reason) : Exception(reason);
