using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit;

public class TranscriptRenumberingTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid = "3c34792ea6ca4082af10c9ac579082f2";

    TranscriptRenumbering Store => new(Config.Root, "https://kcap.example");

    [Test]
    public async Task Every_segment_is_read_back_in_file_order() {
        Store.Record(Sid, [(0, 0), (4, 6), (6, 10)]);

        await Assert.That(Store.Read(Sid)).IsEquivalentTo(new[] { (0, 0), (4, 6), (6, 10) });
    }

    /// <summary>A record replaces the file rather than rewriting it in place, so an import reading it at the same
    /// moment reads the whole of one version or the other, never an empty or partial one.</summary>
    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task A_record_replaces_the_file_a_reader_already_holds() {
        Store.Record(Sid, [(0, 0), (4, 6)]);
        var path = Directory.EnumerateFiles(Config.Root.Path(TranscriptRenumbering.DirectoryName), Sid, SearchOption.AllDirectories).Single();

        await using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Store.Record(Sid, [(0, 0), (4, 6), (6, 10)]);
        using var reader = new StreamReader(held);

        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("0 0\n4 6\n");
    }
}
