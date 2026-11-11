using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Daemon.Pty.Unix;

internal static partial class UnixPtyInterop {
    static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    // forkpty: glibc 2.34+ consolidated libutil into libc, and Ubuntu 24.04 ships only
    // libutil.so.1 (no unversioned libutil.so), so DllImport("libutil") fails to resolve.
    // libc works on Linux (glibc 2.34+) and on macOS (libSystem re-exports forkpty).
    [LibraryImport("libc", EntryPoint = "forkpty", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int forkpty(out int master, IntPtr name, IntPtr termp, ref WinSize winp);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int kill(int pid, int sig);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int waitid(int idtype, int id, byte* infop, int options);

    /// <summary>Whether <paramref name="pid"/> has exited, without reaping it: its zombie keeps its pid and process
    /// group id from being reused. The code is 128 plus the signal for a child a signal killed. -1 with errno
    /// set when the call fails, as for a child already reaped. <c>siginfo_t</c> differs per OS: <c>si_pid</c>
    /// and <c>si_status</c> sit at 16 and 24 on 64-bit Linux, after a padding word, and at 12 and 20 on macOS.</summary>
    public static unsafe int PeekExit(int pid, out bool exited, out int code) {
        const int P_PID = 1, WEXITED = 4, CLD_EXITED = 1;

        var info = stackalloc byte[128];
        new Span<byte>(info, 128).Clear();

        exited = false;
        code   = 0;

        var rc = waitid(P_PID, pid, info, WEXITED | WNOHANG | (IsMacOS ? 0x20 : 0x01000000));
        if (rc != 0) return rc;

        var (pidAt, statusAt) = IsMacOS ? (12, 20) : (16, 24);
        if (*(int*)(info + pidAt) != pid) return 0;

        var status = *(int*)(info + statusAt);
        exited = true;
        code   = *(int*)(info + 8) == CLD_EXITED ? status & 0xFF : 128 + status;

        return 0;
    }

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint read(int fd, byte[] buf, nint count);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint write(int fd, byte[] buf, nint count);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint write(int fd, ReadOnlySpan<byte> buf, nint count);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int poll(ref PollFd fds, uint nfds, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int ioctl(int fd, ulong request, ref WinSize ws);

    [LibraryImport("libc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void _exit(int status);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int tcgetattr(int fd, byte* termios);

    /// <summary>Whether the terminal on <paramref name="fd"/> still rewrites input: canonical mode, or a
    /// CR mapped to LF or dropped. On a master fd this reads the child's side. <c>struct termios</c>
    /// differs per OS: its flag fields are 4 bytes on Linux (<c>c_lflag</c> at offset 12) and 8 on
    /// macOS (<c>c_lflag</c> at 24); <c>c_iflag</c> leads both.</summary>
    public static unsafe bool IsCooked(int fd) {
        const ulong ICRNL = 0x100, IGNCR = 0x80;

        var termios = stackalloc byte[256];
        if (tcgetattr(fd, termios) != 0) return false;

        var (iflag, lflag, icanon) = IsMacOS
            ? (*(ulong*)termios, *(ulong*)(termios + 24), 0x100UL)
            : (*(uint*)termios, *(uint*)(termios + 12), 0x2UL);

        return (lflag & icanon) != 0 || (iflag & (ICRNL | IGNCR)) != 0;
    }

    // On macOS ARM64, ioctl is variadic and P/Invoke doesn't work correctly.
    // Use the native shim if available.
    [LibraryImport("libpty_shim", EntryPoint = "pty_set_winsize", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int pty_set_winsize_shim(int fd, ushort rows, ushort cols);

    [LibraryImport("libpty_shim", EntryPoint = "pty_probe_execveat")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int pty_probe_execveat();

    // argv/envp are NULL-terminated string arrays; a `string?[]` with a trailing `null` element
    // marshals correctly via LibraryImport's array marshaller (a trailing null sentinel, no
    // length prefix — the same shape execve/execvp expect).
    [LibraryImport("libpty_shim", EntryPoint = "pty_preflight", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int pty_preflight(
        string exeAbsPath, string?[] origArgv, string?[] envp, int execveatSupported, out IntPtr outPlan);

    [LibraryImport("libpty_shim", EntryPoint = "pty_plan_contained")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int pty_plan_contained(IntPtr plan);

    [LibraryImport("libpty_shim", EntryPoint = "pty_plan_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void pty_plan_free(ref IntPtr plan);

    [LibraryImport("libc", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int pipe(int[] fds);

    // access(2): the permission-CLASS-aware executability test execvp itself uses to pick the first
    // runnable PATH candidate — owner vs group vs other bits, ACLs, and noexec mounts all factored
    // in, unlike a raw "any execute bit set" mode-mask. Returns 0 when the mode is granted, -1 (with
    // errno) otherwise. Used by the pre-fork PATH resolver so it skips exactly the candidates the
    // child's own exec would skip.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int access(string path, int mode);

    public const int X_OK = 1; // access(2) execute-permission test

    // forkpty + child sequence + error-pipe handshake (L1-shim(b)). `plan` is the opaque
    // pty_exec_plan* from pty_preflight; envp/cwd cross the boundary the same way argv does
    // for pty_preflight — a NULL-terminated `string?[]`/UTF-8 string, no length prefix.
    [LibraryImport("libpty_shim", EntryPoint = "pty_spawn", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int pty_spawn(
        IntPtr plan, string?[] envp, string cwd, ushort rows, ushort cols,
        int expectedParent, int cancelFd, out PtySpawnResult result);

    // macOS-only export (see pty_shim.h) — no compile-time guard needed on the C# side, since
    // LibraryImport resolution is lazy/per-call; callers must gate with OperatingSystem.IsMacOS().
    [LibraryImport("libpty_shim", EntryPoint = "pty_capture_mac_identity", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int pty_capture_mac_identity(int pid, byte* out_, nuint outlen);

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct PtySpawnResult {
        public int  Pid;
        public int  MasterFd;
        public int  ErrNo;
        public int  FailedStep;
        public fixed byte StartIdentity[128];

        public readonly string StartIdentityString {
            get {
                fixed (byte* p = StartIdentity) {
                    var len = 0;
                    while (len < 128 && p[len] != 0) len++;
                    return System.Text.Encoding.UTF8.GetString(p, len);
                }
            }
        }
    }

    public static void SetWinSize(int fd, ushort rows, ushort cols) {
        if (IsMacOS && RuntimeInformation.OSArchitecture == Architecture.Arm64) {
            // Use C shim on macOS ARM64 (ioctl variadic ABI issue)
            pty_set_winsize_shim(fd, rows, cols);
        } else {
            // Direct ioctl on Linux x64 (works fine)
            var ws = new WinSize { ws_row = rows, ws_col = cols };

            const ulong TIOCSWINSZ = 0x5414; // Linux value
            ioctl(fd, TIOCSWINSZ, ref ws);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd {
        public int   fd;
        public short events;
        public short revents;
    }

    public const short POLLIN   = 0x01;
    public const short POLLHUP  = 0x10;
    public const short POLLERR  = 0x08;
    public const short POLLNVAL = 0x20;
    public const int   EINTR    = 4;
    public const int   EIO      = 5;
    public const int   EBADF    = 9;
    public const int   ECHILD   = 10;

    static readonly int EAGAIN = IsMacOS ? 35 : 11;

    /// <summary>An errno after which the same <c>poll</c> or <c>read</c> may simply be issued again:
    /// a handled signal, or (macOS <c>poll</c>) a failed kernel allocation under memory pressure.</summary>
    public static bool IsTransient(int errno) => errno == EINTR || errno == EAGAIN;
    public const int   WNOHANG  = 1;
    public const int   SIGTERM = 15;
    public const int   SIGKILL = 9;
    public const int   SIGINT  = 2;
}
