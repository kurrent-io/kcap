namespace Capacitor.App.Platform.Mac;

/// <summary>
/// Kills a child that is still running this app's own executable after <see cref="Grace"/>: a
/// <c>Process.Start</c> fork that never reached exec. macOS's malloc can wedge such a child in its
/// fork handler forever, and the parent's <c>Process.Start</c> waits for that exec while holding the
/// runtime's process-start lock, so every later spawn in the app blocks behind it and the first one
/// on the UI thread hangs the window. Killing the child ends that wait and the app recovers.
/// </summary>
/// <remarks>Runs on its own thread rather than the pool: each blocked spawn holds a pool thread.</remarks>
public sealed class WedgedForkReaper(IChildProcessProbe probe, TimeProvider time, Action<string> report) {
    /// A healthy fork reaches exec in milliseconds.
    public static readonly TimeSpan Grace  = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(2);

    readonly Dictionary<string, DateTimeOffset> _firstSeen = new(StringComparer.Ordinal);

    /// Returns how many children it killed.
    public int Sweep() {
        if (probe.SelfImage is not { } self) return 0;

        var now     = time.GetUtcNow();
        var present = new HashSet<string>(StringComparer.Ordinal);
        var killed  = 0;

        foreach (var pid in probe.Children()) {
            if (!IsUnexecedFork(pid, self, out var identity)) continue;

            present.Add(identity);
            if (!_firstSeen.TryAdd(identity, now) && now - _firstSeen[identity] >= Grace) {
                // Checked again just before the signal: an exec since the first look changes the image.
                if (!IsUnexecedFork(pid, self, out var current) || current != identity) continue;

                probe.Kill(pid);
                killed++;
                report($"kcap app: killed child {pid}, still a copy of this app {(now - _firstSeen[identity]).TotalSeconds:0}s after fork; it never reached exec");
            }
        }

        foreach (var gone in _firstSeen.Keys.Where(id => !present.Contains(id)).ToList()) _firstSeen.Remove(gone);
        return killed;
    }

    bool IsUnexecedFork(int pid, string self, out string identity) {
        identity = "";
        if (!string.Equals(probe.ImagePath(pid), self, StringComparison.Ordinal)) return false;
        if (probe.Identity(pid) is not { } id) return false;
        identity = id;
        return true;
    }

    public static void Start(IChildProcessProbe probe, TimeProvider time, Action<string> report) {
        var reaper = new WedgedForkReaper(probe, time, report);
        var reportedFailure = false;
        var thread = new Thread(() => {
            while (true) {
                Thread.Sleep(Period);
                try {
                    reaper.Sweep();
                } catch (Exception ex) when (!reportedFailure) {
                    reportedFailure = true;
                    report($"kcap app: fork reaper sweep failed: {ex}");
                } catch {
                    // Already reported once; the next sweep may succeed.
                }
            }
        }) { IsBackground = true, Name = "kcap fork reaper" };
        thread.Start();
    }
}
