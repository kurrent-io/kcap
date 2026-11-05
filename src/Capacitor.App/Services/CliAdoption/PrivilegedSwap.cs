using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>
/// Runs <see cref="PrivilegedSwapScript"/> behind one macOS administrator prompt and takes it through its one decision:
/// once the helper has swapped the links, <c>verify</c> runs as this user, and only a true answer sends go. The helper's
/// pid and start identity are recorded as soon as it reports them, and <see cref="PrivilegedOperation.GoSent"/> before
/// go is written, so a later launch can settle a run this one did not see finish. The caller clears the record once it
/// has acted on the result.
/// </summary>
internal sealed partial class PrivilegedSwap(IProcessRunner runner, AdoptionRecordStore store, TimeProvider? time = null) {
    internal const string OsascriptPath = "/usr/bin/osascript";

    /// The prompt and the script arrive as argv items, and <c>quoted form</c> keeps each argument one word for sh.
    internal const string AppleScript = """
        on run argv
        set command to "/bin/bash -c " & quoted form of item 2 of argv & " kcap-privileged"
        repeat with i from 3 to count of argv
        set command to command & " " & quoted form of item i of argv
        end repeat
        do shell script command with prompt (item 1 of argv) with administrator privileges
        end run
        """;

    readonly TimeProvider _time = time ?? TimeProvider.System;

    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    /// The helper reports swapped a few renames after its pid; one silent for this long is told to put back.
    static readonly TimeSpan SwapWait = TimeSpan.FromSeconds(30);

    /// <summary>Deleted or PutBack when the helper reported either. Without that report, a run that sent go is
    /// CutShort, a cancelled prompt Cancelled, and anything else PutBack: no go was sent, so nothing was deleted.</summary>
    public async Task<PrivilegedResult> RunAsync(
            string installRoot, PrivilegedSpec spec, string prompt, Func<CancellationToken, Task<bool>> verify, CancellationToken ct) {
        string[] args = ["-e", AppleScript, "--", prompt, PrivilegedSwapScript.Render(spec), .. PrivilegedSwapScript.Arguments(spec)];
        FileStream control;
        try {
            CreateChannel(spec);
            control = OpenControl(spec.ControlFifo);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"kcap: could not prepare the administrator helper: {ex.Message}");
            Remove(spec.ControlFifo);
            return PrivilegedResult.PutBack;
        }

        try {
            using (control) {
                var helper = RunHelperAsync(args, ct);
                var goSent = false;
                var word   = PrivilegedSwapScript.PutBack;
                try {
                    var status = await AwaitSwapAsync(spec.StatusFile, helper, ct).ConfigureAwait(false);
                    if (PidOf(status) is { } pid) {
                        var operation = new PrivilegedOperation(installRoot, pid, ProcessStartToken.ForPid(pid), GoSent: false);
                        store.Update(r => r with { Privileged = operation });
                        if (status.Contains(PrivilegedSwapScript.Swapped) && !helper.IsCompleted && await verify(ct).ConfigureAwait(false)) {
                            store.Update(r => r with { Privileged = operation with { GoSent = true } });
                            goSent = true;
                            word   = PrivilegedSwapScript.Go;
                        }
                    }
                } finally {
                    Send(control, word);
                }

                var result    = await helper.ConfigureAwait(false);
                var final     = ReadStatus(spec.StatusFile);
                var cancelled = result is not null && AdministratorPrompt.WasCancelled(result);
                if (result is { ExitCode: not 0 } && !cancelled)
                    Console.Error.WriteLine($"kcap: the administrator helper failed: {result.Stderr.Trim()}");

                return final.Contains(PrivilegedSwapScript.Deleted)  ? PrivilegedResult.Deleted
                     : final.Contains(PrivilegedSwapScript.Restored) ? PrivilegedResult.PutBack
                     : goSent                                        ? PrivilegedResult.CutShort
                     : cancelled                                     ? PrivilegedResult.Cancelled
                     : PrivilegedResult.PutBack;
            }
        } finally {
            Remove(spec.ControlFifo);
            Remove(spec.StatusFile);
        }
    }

    /// <summary>Makes the owner-only control pipe, and clears a status file an earlier run left: the helper refuses to
    /// start over one.</summary>
    internal static void CreateChannel(PrivilegedSpec spec) {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The administrator helper runs on macOS only.");
        foreach (var path in new[] { spec.ControlFifo, spec.StatusFile }) {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Delete(path);
        }
        if (mkfifo(spec.ControlFifo, 0b110_000_000) != 0)
            throw new IOException($"Could not create {spec.ControlFifo}: errno {Marshal.GetLastPInvokeError()}.");
    }

    /// <summary>Opened for reading and writing, which never waits for the helper's end: the helper reads end of file
    /// once this closes, when the app quits or crashes included.</summary>
    internal static FileStream OpenControl(string fifo) =>
        new(fifo, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 0);

    async Task<ProcessResult?> RunHelperAsync(string[] args, CancellationToken ct) {
        try {
            return await runner.RunAsync(OsascriptPath, args, new RunOptions(CancelMode: CancelMode.KillTree), ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) {
            Console.Error.WriteLine($"kcap: could not start the administrator prompt: {ex.Message}");
            return null;
        }
    }

    /// The prompt stays open as long as the user leaves it; once the helper runs, its swap is bounded.
    async Task<string[]> AwaitSwapAsync(string statusFile, Task helper, CancellationToken ct) {
        long? started = null;
        while (true) {
            var status = ReadStatus(statusFile);
            if (status.Contains(PrivilegedSwapScript.Swapped) || status.Contains(PrivilegedSwapScript.Restored) || helper.IsCompleted)
                return status;
            if (PidOf(status) is not null && _time.GetElapsedTime(started ??= _time.GetTimestamp()) > SwapWait)
                return status;

            await Task.WhenAny(helper, Task.Delay(Poll, _time, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    static string[] ReadStatus(string path) {
        try {
            return File.ReadAllLines(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    static int? PidOf(string[] status) =>
        status.Length > 0 && int.TryParse(status[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null;

    static void Send(FileStream control, string word) {
        try {
            control.Write(Encoding.ASCII.GetBytes(word + "\n"));
            control.Flush();
        } catch (IOException ex) {
            Console.Error.WriteLine($"kcap: could not send {word} to the administrator helper: {ex.Message}");
        }
    }

    static void Remove(string path) {
        try {
            File.Delete(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"kcap: could not remove {path}: {ex.Message}");
        }
    }

    // mode_t is 16 bits on macOS.
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int mkfifo(string path, ushort mode);
}
