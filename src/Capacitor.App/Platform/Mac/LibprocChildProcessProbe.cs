using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.App.Platform.Mac;

/// Nothing here may start a process: it runs while every Process.Start in the app is blocked.
[SupportedOSPlatform("macos")]
public sealed partial class LibprocChildProcessProbe : IChildProcessProbe {
    const int MaxChildren = 4096;
    const int PathMax     = 4096; // PROC_PIDPATHINFO_MAXSIZE
    const int SigKill     = 9;

    public string? SelfImage { get; } = ReadImagePath(Environment.ProcessId);

    public unsafe IReadOnlyList<int> Children() {
        var pids = new int[MaxChildren];
        int count;
        fixed (int* p = pids) count = proc_listchildpids(Environment.ProcessId, p, pids.Length * sizeof(int));
        return count <= 0 ? [] : pids[..Math.Min(count, pids.Length)];
    }

    public string? ImagePath(int pid) => ReadImagePath(pid);

    public string? Identity(int pid) => ProcessStartToken.ForPid(pid);

    public void Kill(int pid) => kill(pid, SigKill);

    static unsafe string? ReadImagePath(int pid) {
        var buffer = new byte[PathMax];
        int length;
        fixed (byte* p = buffer) length = proc_pidpath(pid, p, (uint) buffer.Length);
        return length <= 0 ? null : Encoding.UTF8.GetString(buffer, 0, length);
    }

    // Returns the number of pids written, not bytes.
    [LibraryImport("libproc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int proc_listchildpids(int ppid, int* buffer, int buffersize);

    [LibraryImport("libproc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int proc_pidpath(int pid, byte* buffer, uint buffersize);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int kill(int pid, int sig);
}
