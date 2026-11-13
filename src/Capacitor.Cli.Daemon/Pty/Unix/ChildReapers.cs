using System.Runtime.CompilerServices;

namespace Capacitor.Cli.Daemon.Pty.Unix;

/// <summary>Whether something besides <see cref="UnixPtyProcess"/> can reap its children. .NET's SIGCHLD handler
/// reaps every child with waitpid(-1) when the process is pid 1, or when SIGCHLD was ignored before the runtime
/// replaced it, and an unreaped zombie is all that keeps a group id from being reused. The runtime keeps the
/// original disposition to itself, so it is read before anything can make the runtime install its handler; a
/// handler already installed by then hides it, and is taken as a reaper.</summary>
static class ChildReapers {
    const nint Default = 0;

    public static bool RuntimeMayReap { get; private set; } = true;

    [ModuleInitializer]
    internal static void Capture() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        RuntimeMayReap = Decide(Environment.ProcessId, UnixPtyInterop.SigChldHandler());
    }

    /// <summary>Only the default disposition rules the runtime out: ignored makes it reap everything, and a
    /// handler, or a failed read, leaves the original unknown.</summary>
    internal static bool Decide(int pid, nint sigChldHandler) => pid == 1 || sigChldHandler != Default;
}
