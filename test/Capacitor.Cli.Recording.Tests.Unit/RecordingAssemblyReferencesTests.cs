namespace Capacitor.Cli.Recording.Tests.Unit;

public class RecordingAssemblyReferencesTests {
    static readonly string?[] References =
        [..typeof(TranscriptRecorder).Assembly.GetReferencedAssemblies().Select(reference => reference.Name)];

    /// <summary>The daemon hosts recorders and can't reference <c>kcap</c>.</summary>
    [Test]
    public async Task The_recording_assembly_does_not_reference_kcap() =>
        await Assert.That(References).DoesNotContain("kcap");

    /// <summary>The AOT daemon references this assembly, and SQLite's native bundle must never reach it.</summary>
    [Test]
    public async Task The_recording_assembly_does_not_reference_sqlite() =>
        await Assert.That(References).DoesNotContain("Microsoft.Data.Sqlite");
}
