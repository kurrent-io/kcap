using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Capacitor.Cli.Core;

namespace Capacitor.App.Platform.Mac;

/// Nothing here may start a process: it runs while every Process.Start in the app is blocked.
[SupportedOSPlatform("macos")]
public sealed partial class LibprocChildProcessProbe : IChildProcessProbe {
    const int MaxChildren = 4096;
    const int SigKill     = 9;

    // sys/proc_info.h: PROC_PIDT_SHORTBSDINFO fills a 64-byte proc_bsdshortinfo.
    const int  ProcPidTShortBsdInfo = 13;
    const int  ShortBsdInfoSize     = 64;
    const int  StatusOffset         = 12; // pbsi_status
    const int  FlagsOffset          = 32; // pbsi_flags
    const uint ProcFlagExec         = 0x4000; // set by exec, never inherited across fork
    const uint StatusZombie         = 5;      // SZOMB

    public unsafe IReadOnlyList<int> Children() {
        var pids = new int[MaxChildren];
        int count;
        fixed (int* p = pids) count = proc_listchildpids(Environment.ProcessId, p, pids.Length * sizeof(int));
        return count <= 0 ? [] : pids[..Math.Min(count, pids.Length)];
    }

    public unsafe bool? HasExeced(int pid) {
        Span<byte> info = stackalloc byte[ShortBsdInfoSize];
        int written;
        fixed (byte* p = info) written = proc_pidinfo(pid, ProcPidTShortBsdInfo, 0, p, ShortBsdInfoSize);
        if (written != ShortBsdInfoSize) return null;
        if (BitConverter.ToUInt32(info[StatusOffset..]) == StatusZombie) return null;
        return (BitConverter.ToUInt32(info[FlagsOffset..]) & ProcFlagExec) != 0;
    }

    public string? Identity(int pid) => ProcessStartToken.ForPid(pid);

    public void Kill(int pid) => kill(pid, SigKill);

    // Returns the number of pids written, not bytes.
    [LibraryImport("libproc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int proc_listchildpids(int ppid, int* buffer, int buffersize);

    [LibraryImport("libproc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int proc_pidinfo(int pid, int flavor, ulong arg, byte* buffer, int buffersize);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int kill(int pid, int sig);
}
