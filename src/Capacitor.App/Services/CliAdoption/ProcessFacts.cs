using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Capacitor.App.Services.Update;
using Capacitor.Cli.Core.Processes;

namespace Capacitor.App.Services.CliAdoption;

internal static partial class ProcessFacts {
    // sys/proc_info.h: PROC_UID_ONLY filters on the effective uid.
    const uint ProcUidOnly = 4;

    /// <summary>This user's processes on macOS, empty elsewhere. A process that is gone or cannot be read by the time
    /// it is asked about is left out; arguments are read only for a JavaScript runtime, whose script names its agent.</summary>
    internal static IReadOnlyList<ProcessFact> ForCurrentUser() {
        if (!OperatingSystem.IsMacOS()) return [];

        var facts = new List<ProcessFact>();
        foreach (var pid in Pids()) {
            if (Read(pid) is { } fact) facts.Add(fact);
        }

        return facts;
    }

    static ProcessFact? Read(int pid) {
        try {
            DateTime started;
            using (var process = Process.GetProcessById(pid)) started = process.StartTime.ToUniversalTime();

            var executable = ProcessExecutable.Of(pid);
            var args       = executable is not null && ProcessArgs.IsScriptRuntime(executable) ? ProcessArgs.Read(pid) : null;

            return new ProcessFact(pid, executable, args, started);
        } catch (Exception) {
            return null;
        }
    }

    // Returns bytes, not a count. A full buffer may have been cut short, so it is grown and asked again.
    static unsafe int[] Pids() {
        var uid  = getuid();
        var size = proc_listpids(ProcUidOnly, uid, null, 0);
        if (size <= 0) return [];

        var buffer = new int[size / sizeof(int) + 64];
        while (true) {
            int written;
            fixed (int* p = buffer) written = proc_listpids(ProcUidOnly, uid, p, buffer.Length * sizeof(int));
            if (written <= 0) return [];

            var count = written / sizeof(int);
            if (count < buffer.Length) return [.. buffer[..count].Where(pid => pid > 0)];

            buffer = new int[buffer.Length * 2];
        }
    }

    [LibraryImport("libproc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int proc_listpids(uint type, uint typeinfo, int* buffer, int buffersize);

    [LibraryImport("libc")]
    private static partial uint getuid();
}
