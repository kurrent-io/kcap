using System.Diagnostics;

namespace Capacitor.Cli.Core.RepoInstructions;

/// <summary>Runs git in a checkout and hands back its standard output as bytes, untrimmed, so a file's content arrives
/// exactly as committed. Null when git fails, exits non-zero, outlives <see cref="Timeout"/> or writes more than
/// <see cref="MaxBytes"/>.</summary>
internal static class GitOutput {
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal const int MaxBytes = 4 * 1024 * 1024;

    public static async Task<byte[]?> RunAsync(string cwd, TimeProvider time, CancellationToken ct, params string[] arguments) {
        var psi = new ProcessStartInfo("git") {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        Process? process = null;
        try {
            process = Process.Start(psi);
            if (process is null) return null;

            using var timeout = new CancellationTokenSource(Timeout, time);
            using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var buffer  = new MemoryStream();
            var       stderr  = process.StandardError.ReadToEndAsync(linked.Token);
            var       chunk   = new byte[81920];
            int       read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(chunk, linked.Token)) > 0) {
                if (buffer.Length + read > MaxBytes) {
                    Kill(process);
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }
            await process.WaitForExitAsync(linked.Token);
            await stderr;

            return process.ExitCode == 0 ? buffer.ToArray() : null;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            Kill(process);
            throw;
        } catch {
            Kill(process);
            return null;
        } finally {
            process?.Dispose();
        }
    }

    static void Kill(Process? process) {
        try { if (process is { HasExited: false }) ProcessTree.Kill(process); } catch { /* ignored */ }
    }
}
