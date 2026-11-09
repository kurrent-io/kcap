using System.Runtime.Versioning;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class RecordedSourceListTests {
    const string Session = "0123456789abcdef0123456789abcdef";

    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>A removal the disk refused is still owed, and the next call writes it whatever key that call is for: a
    /// restart reading the old list would pick the source up again.</summary>
    [Test]
    [ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task A_removal_the_disk_refused_is_written_by_the_next_call() {
        var state  = Tmp.CreateDir("state");
        var list   = new RecordedSourceList(state, NullLogger.Instance);
        var source = new RecordedSource("agent-1", Session, null, Tmp.PathTo("t.jsonl"), "claude", null, false);
        list.TryAdd(source);

        File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try {
            list.Remove(source.Key);
        } finally {
            File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        list.Remove("a key nothing listed");

        await Assert.That(new RecordedSourceList(state, NullLogger.Instance).Load()).IsEmpty();
    }
}
