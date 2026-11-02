namespace Capacitor.App.Platform.Mac;

/// <summary>
/// Kills a child that is still a bare fork of this app — it has not called exec — after
/// <see cref="Grace"/>. macOS's malloc can wedge such a child in its fork handler forever, and the
/// parent's <c>Process.Start</c> waits for that exec while holding the runtime's process-start lock,
/// so every later spawn in the app blocks behind it and the first one on the UI thread hangs the
/// window. Killing the child ends that wait and the app recovers.
/// </summary>
/// <remarks>
/// The check and the signal cannot be atomic: a child that finally execs in between is killed
/// anyway, and its caller sees a run that died of SIGKILL — a failure, never a false success.
/// Runs on its own thread rather than the pool: each blocked spawn holds a pool thread.
/// </remarks>
public sealed class WedgedForkReaper(IChildProcessProbe probe, TimeProvider time, Action<string> report) {
    /// A healthy fork reaches exec in milliseconds.
    public static readonly TimeSpan Grace  = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(2);

    readonly Dictionary<string, long> _firstSeen = new(StringComparer.Ordinal);
    readonly HashSet<string>          _killed    = new(StringComparer.Ordinal);

    /// Returns how many children it killed.
    public int Sweep() {
        var present = new HashSet<string>(StringComparer.Ordinal);
        var killed  = 0;

        foreach (var pid in probe.Children()) {
            if (UnexecedIdentity(pid) is not { } identity) continue;

            present.Add(identity);
            if (_firstSeen.TryAdd(identity, time.GetTimestamp()) || _killed.Contains(identity)) continue;

            var age = time.GetElapsedTime(_firstSeen[identity]);
            if (age < Grace) continue;
            if (UnexecedIdentity(pid) != identity) continue;

            probe.Kill(pid);
            _killed.Add(identity);
            killed++;
            report($"kcap app: killed child {pid}, {age.TotalSeconds:0}s after fork without reaching exec");
        }

        foreach (var gone in _firstSeen.Keys.Where(id => !present.Contains(id)).ToList()) {
            _firstSeen.Remove(gone);
            _killed.Remove(gone);
        }
        return killed;
    }

    string? UnexecedIdentity(int pid) => probe.HasExeced(pid) == false ? probe.Identity(pid) : null;

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
