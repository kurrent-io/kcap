using LoadedAssembly = System.Reflection.Assembly;

namespace Capacitor.Cli.Recording.Tests.Unit;

public class RecordingAssemblyReferencesTests {
    static readonly string?[] References =
        [..typeof(TranscriptRecorder).Assembly.GetReferencedAssemblies().Select(reference => reference.Name)];

    /// <summary>The daemon hosts recorders and can't reference <c>kcap</c>.</summary>
    [Test]
    public async Task The_recording_assembly_does_not_reference_kcap() =>
        await Assert.That(References).DoesNotContain("kcap");

    /// <summary>A daemon's AOT build must never carry SQLite's native bundle, so no assembly this one reaches,
    /// directly or through another, may be SQLite.</summary>
    [Test]
    public async Task The_recording_assembly_does_not_reach_sqlite() =>
        await Assert.That(ReachableAssemblyNames(typeof(TranscriptRecorder).Assembly).Where(IsSqlite)).IsEmpty();

    /// <summary>The walk has to follow a reference, or a SQLite one added to Core would pass.</summary>
    [Test]
    public async Task The_reference_walk_reaches_what_core_references() {
        var reached = ReachableAssemblyNames(typeof(TranscriptRecorder).Assembly);
        var core    = typeof(Capacitor.Cli.Core.WatchState).Assembly.GetReferencedAssemblies().Select(reference => reference.Name!);

        await Assert.That(core.Except(reached)).IsEmpty();
    }

    static bool IsSqlite(string name) =>
        name.StartsWith("Microsoft.Data.Sqlite", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("SQLitePCL", StringComparison.OrdinalIgnoreCase);

    static HashSet<string> ReachableAssemblyNames(LoadedAssembly root) {
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.GetName().Name! };
        var pending = new Stack<LoadedAssembly>([root]);

        while (pending.TryPop(out var assembly)) {
            foreach (var reference in assembly.GetReferencedAssemblies()) {
                if (reference.Name is not { } name || !seen.Add(name)) continue;

                try { pending.Push(LoadedAssembly.Load(reference)); } catch (Exception e) when (e is IOException or BadImageFormatException) { /* named, not followed */ }
            }
        }

        return seen;
    }
}
