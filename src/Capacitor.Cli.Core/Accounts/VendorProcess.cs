using System.ComponentModel;
using System.Diagnostics;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>What the account probes share when they start a vendor CLI.</summary>
static class VendorProcess {
    /// <summary>Points the child at the account's directory, or removes the variable for the vendor's
    /// built-in one.</summary>
    public static void PlanHome(ProcessStartInfo psi, HarnessId vendor, string directory, UserHome home) {
        if (VendorHomeVariable.Plan(vendor, directory, home) is not { } plan) return;

        var (variable, value) = plan;
        if (value is null) psi.Environment.Remove(variable);
        else psi.Environment[variable] = value;
    }

    static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(2);

    public static async Task ShutDownAsync(Process process, TimeProvider time) {
        try {
            process.StandardInput.Close();
            using var grace = new CancellationTokenSource(ExitGrace, time);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        } catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException) {
            // Still running, or its input already gone: the kill below settles it.
        }

        Stop(process);
    }

    /// <summary>Kills the direct child only: a tree kill SIGSTOPs each process before killing it, and the
    /// daemon, which runs these probes too, must never stop a member of its own process group.</summary>
    public static void Stop(Process process) {
        try {
            if (!process.HasExited) process.Kill();
        } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) {
            // Already exited, or not ours to signal.
        }
    }
}
