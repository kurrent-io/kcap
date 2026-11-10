namespace Capacitor.Cli.Core.Setup;

/// <summary>Tri-state result of probing a launchd service via launchctl print.</summary>
public enum LabelProbe { Loaded, Absent, Unknown }
