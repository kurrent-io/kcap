namespace Capacitor.App.Platform.Windows;

/// A running process as a scan saw it. The start time is its identity: a pid the system reused names another process.
public sealed record RunningImage(int Pid, string Path, DateTime StartTime);
