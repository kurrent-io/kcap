using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit;

public class RepositoryDetectionProbeTimeoutTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>A timed-out probe's children go with it, or slow git and gh runs pile up across hooks.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task A_timed_out_probe_dies_with_its_children() {
        var pids  = Tmp.PathTo("pids");
        var probe = Tmp.CreateExecutable("probe", $$"""
            #!/bin/sh
            sleep 30 &
            echo "$$ $!" > "{{pids}}.tmp"
            mv "{{pids}}.tmp" "{{pids}}"
            wait
            """);

        var time    = new FakeTimeProvider();
        var timeout = TimeSpan.FromSeconds(1);
        var running = RepositoryDetection.DefaultRunner(time)(probe, "", Tmp.Path, timeout);

        await WaitForAsync(pids).WithinDeadlineAsync("the probe to start");
        var alive = (await File.ReadAllTextAsync(pids)).Split(' ', StringSplitOptions.TrimEntries).Select(int.Parse)
            .Select(pid => (Pid: pid, Identity: PidIdentity.Capture(pid))).ToArray();

        time.Advance(timeout);

        await Assert.That(await running).IsNull();
        foreach (var (pid, identity) in alive) await PidIdentity.WaitUntilGoneAsync(pid, identity, TimeSpan.FromSeconds(5));
    }

    static async Task WaitForAsync(string path) {
        while (!File.Exists(path)) await Task.Delay(10);
    }
}
