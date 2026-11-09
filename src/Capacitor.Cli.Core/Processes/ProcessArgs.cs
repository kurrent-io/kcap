using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Capacitor.Cli.Core.Processes;

/// <summary>Another process's arguments, and the script a JavaScript runtime among them runs.</summary>
public static partial class ProcessArgs {
    // sysctl({CTL_KERN, KERN_PROCARGS2, pid}) returns argc + the kernel's recorded
    // executable path + argv + env for a process. The exec path is immune to a process
    // changing its title, so it is the reliable name source for the agent ancestry walk.
    [LibraryImport("libc", EntryPoint = "sysctl", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int sysctl(int* name, uint namelen, byte* oldp, nuint* oldlenp, byte* newp, nuint newlen);

    const int CtlKern          = 1;
    const int KernProcArgs2    = 49;

    /// <summary>The arguments of <paramref name="pid"/> on macOS, or null when they cannot be read.</summary>
    public static IReadOnlyList<string>? Read(int pid) => ReadBuffer(pid) is { } buffer ? Parse(buffer) : null;

    /// <summary>
    /// Returns the <c>KERN_PROCARGS2</c> buffer of <paramref name="pid"/> — the same source <c>ps</c>
    /// uses — or null if it can't be read. Unlike <c>proc_bsdinfo</c>'s name fields, its exec path is
    /// the kernel's recorded image path and is not affected by a process changing its title, so it
    /// reliably identifies the coding agent for the ancestry walk.
    /// </summary>
    public static unsafe byte[]? ReadBuffer(int pid) {
        if (!OperatingSystem.IsMacOS()) {
            return null;
        }

        Span<int> mib = [CtlKern, KernProcArgs2, pid];
        nuint     size = 0;

        fixed (int* m = mib) {
            // First call (oldp = null) reports the buffer size to allocate. The buffer
            // holds argc + exec_path + argv + env, so it can't be guessed up front.
            if (sysctl(m, 3, null, &size, null, 0) != 0 || size == 0) {
                return null;
            }

            var buf = new byte[size];

            fixed (byte* b = buf) {
                // Reuse the same `size` as the buffer length; a short buffer would make
                // sysctl fail with ENOMEM rather than truncate, so we'd just fall back.
                if (sysctl(m, 3, b, &size, null, 0) != 0) {
                    return null;
                }
            }

            return buf.AsSpan(0, (int)size).ToArray();
        }
    }

    /// <summary>
    /// The arguments in a <c>KERN_PROCARGS2</c> buffer: after <c>argc</c> and the exec path comes NUL
    /// padding, then <c>argc</c> NUL-terminated strings. Pure so it is unit-testable; empty when the
    /// buffer is too short to hold them.
    /// </summary>
    public static IReadOnlyList<string> Parse(ReadOnlySpan<byte> procArgs2) {
        if (procArgs2.Length <= sizeof(int)) return [];

        var argc = BitConverter.ToInt32(procArgs2[..sizeof(int)]);
        var rest = procArgs2[sizeof(int)..];
        var nul  = rest.IndexOf((byte)0);
        if (nul < 0) return [];

        // Only the padding after the exec path is skipped: past it each NUL ends one argument, an empty one
        // included, so counting never runs on into the environment.
        var first = rest[nul..].IndexOfAnyExcept((byte)0);
        if (first < 0) return [];
        rest = rest[(nul + first)..];
        var args = new List<string>(Math.Clamp(argc, 0, 16));
        while (args.Count < argc && !rest.IsEmpty) {
            var end = rest.IndexOf((byte)0);
            args.Add(Encoding.UTF8.GetString(end >= 0 ? rest[..end] : rest));
            if (end < 0) break;
            rest = rest[(end + 1)..];
        }
        return args;
    }

    /// <summary>
    /// The names a JavaScript runtime's script identifies its agent by. Gemini CLI runs as <c>node</c>
    /// and sets no title, so only the script it runs names it: the <c>gemini</c> shim the shebang
    /// hands over, or the entry point inside its installed package. A script file of any other kind,
    /// such as <c>codex.js</c>, names nothing: it is an ordinary script, not an installed agent.
    /// </summary>
    public static IEnumerable<string> ScriptNameCandidates(IReadOnlyList<string> argv) {
        if (argv.Count < 2 || !IsScriptRuntime(argv[0])) yield break;
        if (Script(argv) is not { } script) yield break;

        if (!Path.HasExtension(script.Replace('\\', '/').Split('/')[^1]))
            foreach (var candidate in AgentProcessNames.Candidates(script)) yield return candidate;

        // An installed package's entry point is …/node_modules/[@scope/]<package>/….
        var parts = script.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var at    = Array.FindLastIndex(parts, part => part.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= parts.Length) yield break;
        yield return parts[at + 1].StartsWith('@') && at + 2 < parts.Length ? parts[at + 2] : parts[at + 1];
    }

    /// The runtime's options that take their value as the next argument, so that value is not the script.
    static readonly FrozenSet<string> ValueOptions = new[] {
        "-r", "--require", "--import", "--loader", "--experimental-loader", "-C", "--conditions", "--input-type",
        "--inspect-port", "--debug-port", "--title", "--env-file", "--env-file-if-exists", "--watch-path",
        "--redirect-warnings", "--openssl-config", "--icu-data-dir", "--diagnostic-dir", "--report-dir",
        "--report-directory", "--report-filename", "--heapsnapshot-signal", "--secure-heap", "--secure-heap-min",
        "--cwd", "--config", "--preload"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// The script the runtime runs, or null when it runs code given inline or on stdin.
    public static string? Script(IReadOnlyList<string> argv) {
        for (var i = 1; i < argv.Count; i++) {
            var arg = argv[i];
            if (arg is "--") return i + 1 < argv.Count ? argv[i + 1] : null;
            if (arg is "-" or "-e" or "--eval" or "-p" or "--print"
                || arg.StartsWith("--eval=", StringComparison.Ordinal) || arg.StartsWith("--print=", StringComparison.Ordinal)
                || (arg.Length > 2 && arg[0] == '-' && arg[1] != '-' && arg.AsSpan(1).IndexOfAny('e', 'p') >= 0)) return null;
            if (ValueOptions.Contains(arg)) { i++; continue; }
            if (arg.StartsWith('-') || (i == 1 && arg == "run")) continue;
            return arg;
        }
        return null;
    }

    public static bool IsScriptRuntime(string argv0) {
        var name = Path.GetFileNameWithoutExtension(argv0.Replace('\\', '/').Split('/')[^1]);
        return name.Equals("node", StringComparison.OrdinalIgnoreCase) || name.Equals("nodejs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bun", StringComparison.OrdinalIgnoreCase);
    }
}
