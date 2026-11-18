using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>What a terminal hook reads before it opens a daemon's socket: present only while that daemon runs with
/// recording on.</summary>
public class DaemonRecordingMarkerTests {
    const string Name = "daemon-a";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task Recording_on_writes_the_marker_and_off_removes_it() {
        DaemonRecordingMarker.Sync(Daemons.Store, Name, recording: true);
        await Assert.That(DaemonRecordingMarker.Exists(Daemons.Store, Name)).IsTrue();

        DaemonRecordingMarker.Sync(Daemons.Store, Name, recording: false);
        await Assert.That(DaemonRecordingMarker.Exists(Daemons.Store, Name)).IsFalse();
    }

    [Test]
    public async Task Another_daemons_marker_is_not_this_ones() {
        DaemonRecordingMarker.Sync(Daemons.Store, "daemon-b", recording: true);

        await Assert.That(DaemonRecordingMarker.Exists(Daemons.Store, Name)).IsFalse();
    }

    [Test, ExcludeOn(OS.Windows)]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task The_marker_is_owner_only() {
        DaemonRecordingMarker.Sync(Daemons.Store, Name, recording: true);

        await Assert.That(File.GetUnixFileMode(Daemons.Store.RecordingMarkerPath(Name))).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
