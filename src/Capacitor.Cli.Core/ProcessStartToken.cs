using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Core;

/// <summary>
/// A machine-local token identifying a process incarnation, identical for every reader process.
/// Stored on the second line of the daemon PID file to tell a live daemon from a recycled PID.
/// <para>Not <see cref="Process.StartTime"/>: on Linux .NET derives it from a per-call boot-time
/// estimate, so two processes read different values for the same PID.</para>
/// <para>Linux: <c>lx:&lt;boot_id&gt;:&lt;starttime&gt;</c>, field 22 of <c>/proc/&lt;pid&gt;/stat</c>
/// scoped by the boot id. macOS: <c>mac:&lt;kern.bootsessionuuid&gt;:&lt;p_uniqueid&gt;</c>, read via
/// <c>PROC_PIDUNIQIDENTIFIERINFO</c>; this duplicates <c>pty_shim.c</c>'s capture because the CLI has no
/// native shim, and a consistency test keeps the two in step. Windows: <c>tk:&lt;ticks&gt;</c>, since
/// its start time is already absolute.</para>
/// <para>A PID file written by an older daemon stores bare ticks with no <c>scheme:</c> prefix;
/// <see cref="Matches"/> returns null for it so callers fall back to an image-name check instead of
/// stranding a live daemon.</para>
/// </summary>
public static partial class ProcessStartToken {
    /// <summary>Token for the calling process, or null if it can't be read.</summary>
    public static string? ForCurrent() => ForPid(Environment.ProcessId);

    /// <summary>
    /// Token for <paramref name="pid"/>, or null if the process is gone or the
    /// value can't be read.
    /// </summary>
    public static string? ForPid(int pid) {
        if (OperatingSystem.IsLinux()) {
            var starttime = ReadLinuxStartTicks(pid);

            return starttime is null ? null : $"lx:{LinuxBootId()}:{starttime}";
        }

        if (OperatingSystem.IsMacOS()) {
            // A live process must exist for this pid before trying the private-ABI capture —
            // proc_pidinfo on a gone pid returns a short/garbage read anyway, but checking via
            // Process first means a nonexistent pid returns null through the SAME path as every
            // other platform (a consistent contract for callers), and only THEN is the mac:
            // capture attempted. Falling through to tk: if that capture specifically fails would
            // be WRONG (it would silently downgrade a live, CONFIRMED-alive process to the weaker
            // wall-clock scheme) — so a capture failure here returns null (uncapturable),
            // matching the Linux contract's "value can't be read" case, never a scheme fallback.
            try {
                using var proc = Process.GetProcessById(pid);

                return CaptureMacToken(pid);
            } catch {
                return null;
            }
        }

        try {
            using var process = Process.GetProcessById(pid);

            return $"tk:{process.StartTime.ToUniversalTime().Ticks}";
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Tri-state comparison of <paramref name="expectedToken"/> (from a PID
    /// file) against the live process at <paramref name="pid"/>:
    /// <list type="bullet">
    /// <item><c>true</c> — same scheme, same value: it IS that incarnation.</item>
    /// <item><c>false</c> — same scheme, different value: a different incarnation
    /// (e.g. a recycled PID). Conclusive; do not fall back.</item>
    /// <item><c>null</c> — can't compare (process gone, token unreadable, or
    /// <paramref name="expectedToken"/> is a legacy/foreign scheme). Callers
    /// should use a weaker check rather than treat the PID as not-ours.</item>
    /// </list>
    /// </summary>
    public static bool? Matches(int pid, string expectedToken) {
        var actual = ForPid(pid);

        if (actual is null || !SameScheme(actual, expectedToken)) return null;

        return string.Equals(actual, expectedToken, StringComparison.Ordinal);
    }

    /// <summary>Two tokens share a scheme when their text up to the first ':' matches.</summary>
    static bool SameScheme(string a, string b) {
        var ia = a.IndexOf(':');
        var ib = b.IndexOf(':');

        return ia > 0 && ib > 0 && a.AsSpan(0, ia).SequenceEqual(b.AsSpan(0, ib));
    }

    /// <summary>Per-boot UUID, or "?" if unreadable (consistent across readers within a boot).</summary>
    static string LinuxBootId() {
        try {
            return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        } catch {
            return "?";
        }
    }

    /// <summary>
    /// Reads field 22 (<c>starttime</c>) from <c>/proc/&lt;pid&gt;/stat</c>. The
    /// line is <c>pid (comm) state ppid ...</c> where <c>comm</c> can itself
    /// contain spaces and <c>)</c>, so we split on the text after the LAST
    /// <c>)</c>: those tokens begin at field 3 (<c>state</c>), making
    /// <c>starttime</c> index 19.
    /// </summary>
    static string? ReadLinuxStartTicks(int pid) {
        try {
            var stat      = File.ReadAllText($"/proc/{pid}/stat");
            var afterComm = stat.LastIndexOf(')');

            if (afterComm < 0 || afterComm + 2 >= stat.Length) return null;

            var fields = stat[(afterComm + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return fields.Length > 19 ? fields[19] : null;
        } catch {
            return null;
        }
    }

    // ── macOS: PROC_PIDUNIQIDENTIFIERINFO (flavor 17) is private in the SDK headers.
    //
    // The struct's total size varies across OS versions; only its prefix is stable: p_uuid[16], then
    // p_uniqueid at offset 16, then p_puniqueid at 24. So this reads a generous 256-byte buffer and
    // requires only the prefix. pty_shim.c's pty_capture_mac_identity must read the same bytes at the
    // same offsets, or the daemon's spawn-time capture and this reap-time read disagree about one
    // process; ProcessStartTokenTests.ForPid_OnMacOS_MatchesNativeShimCaptureForSelf guards it. CI has
    // no macOS leg running it. A violated assumption yields null, never a wrong value.
    const int ProcPidUniqIdentifierInfo   = 17;
    const int UniqIdentifierInfoBufSize   = 256; // matches pty_shim.c's PTY_UNIQIDENTIFIERINFO_BUFSIZE
    const int UniqIdentifierInfoPrefixLen = 32;  // p_uuid[16] + p_uniqueid(8) + p_puniqueid(8) — the only verified-stable prefix

    [LibraryImport("libproc", EntryPoint = "proc_pidinfo")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int proc_pidinfo(int pid, int flavor, ulong arg, byte* buffer, int buffersize);

    [LibraryImport("libc", EntryPoint = "sysctlbyname", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int sysctlbyname(string name, byte* oldp, nuint* oldlenp, IntPtr newp, nuint newlen);

    /// <summary>
    /// Vendored macOS capture: <c>mac:&lt;kern.bootsessionuuid&gt;:&lt;p_uniqueid&gt;</c>, or null
    /// if any step is uncapturable. Fail-safe is SPARE-shaped throughout: any short/negative read
    /// (kernel refusal, EINVAL, gone pid), a zero unique id, or a failed <c>sysctlbyname</c> call
    /// returns null — NEVER a wrong/garbage token.
    /// </summary>
    static unsafe string? CaptureMacToken(int pid) {
        var buf = new byte[UniqIdentifierInfoBufSize];
        int written;

        fixed (byte* p = buf) {
            written = proc_pidinfo(pid, ProcPidUniqIdentifierInfo, 0, p, buf.Length);
        }

        // Fail-safe: only a read that covers at least the verified prefix proves anything. A
        // short read (kernel's min-size refusal), a negative return (error), or anything else
        // that doesn't reach the prefix is uncapturable — never a false proof, never a
        // wrong-offset token from a struct shape we haven't verified.
        if (written < UniqIdentifierInfoPrefixLen) return null;

        var uniqueId = BitConverter.ToUInt64(buf, 16); // p_uuid[16] then p_uniqueid @ offset 16

        if (uniqueId == 0) return null;

        Span<byte> uuidBuf = stackalloc byte[64];
        nuint      uuidLen = (nuint) uuidBuf.Length;

        fixed (byte* up = uuidBuf) {
            if (sysctlbyname("kern.bootsessionuuid", up, &uuidLen, IntPtr.Zero, 0) != 0) return null;
        }

        if (uuidLen == 0) return null;

        var uuidStr = System.Text.Encoding.UTF8.GetString(uuidBuf[..(int) (uuidLen - 1)]); // drop the trailing NUL

        return $"mac:{uuidStr}:{uniqueId}";
    }
}
