using Capacitor.App.Services;
using Capacitor.Cli.Core;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

public class EntryIdentityTests {
    [TempDir] public required TempDir Tmp { get; init; }

    readonly EntryIdentity _identity = new(new ProcessRunner(TimeProvider.System));

    [Test, RunOn(OS.MacOs)]
    public async Task A_file_replaced_at_the_same_path_reads_differently() {
        var file = Tmp.CreateFile("kcap-daemon", "one");
        var before = await _identity.OfAsync(file, CancellationToken.None);

        var next = Tmp.CreateFile("next", "two");
        File.Move(next, file, overwrite: true);

        await Assert.That(before).IsNotNull();
        await Assert.That(await _identity.OfAsync(file, CancellationToken.None)).IsNotEqualTo(before);
    }

    [Test, RunOn(OS.MacOs)]
    public async Task A_missing_entry_has_no_identity() {
        await Assert.That(await _identity.OfAsync(Tmp.PathTo("gone"), CancellationToken.None)).IsNull();
    }

    [Test, RunOn(OS.MacOs)]
    public async Task A_link_reads_as_itself_not_its_target() {
        var target = Tmp.CreateFile("target");
        var link = Tmp.PathTo("link");
        File.CreateSymbolicLink(link, target);

        await Assert.That(await _identity.OfAsync(link, CancellationToken.None)).IsNotEqualTo(await _identity.OfAsync(target, CancellationToken.None));
    }
}
