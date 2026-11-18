using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit.Services;

/// Recent base refs are kept per machine scope and repository, newest first, capped, and survive
/// the ordinal comparer a JSON round-trip gives the dictionary.
public class BaseRefMemoryTests {
    static readonly StringComparer Ordinal = StringComparer.Ordinal;

    [Test]
    public async Task Remembering_moves_a_ref_to_the_front_and_caps_the_list() {
        IReadOnlyDictionary<string, IReadOnlyList<string>>? refs = null;
        foreach (var name in new[] { "a", "b", "c", "d", "e", "f", "c" })
            refs = BaseRefMemory.Remember(refs, "local", "/r", Ordinal, name);

        await Assert.That(BaseRefMemory.Lookup(refs, "local", "/r", Ordinal)).IsEquivalentTo(
            new[] { "c", "f", "e", "d", "b" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Machines_and_repositories_keep_separate_lists() {
        var refs = BaseRefMemory.Remember(null, "local", "/r", Ordinal, "main");
        refs = BaseRefMemory.Remember(refs, BaseRefMemory.ScopeFor("box", remote: true), "/r", Ordinal, "dev");

        await Assert.That(BaseRefMemory.Lookup(refs, "local", "/r", Ordinal)).IsEquivalentTo(new[] { "main" });
        await Assert.That(BaseRefMemory.Lookup(refs, "remote:box", "/r", Ordinal)).IsEquivalentTo(new[] { "dev" });
        await Assert.That(BaseRefMemory.Lookup(refs, "local", "/other", Ordinal)).IsEmpty();
    }

    [Test]
    public async Task The_path_is_matched_under_the_callers_comparer() {
        var refs = BaseRefMemory.Remember(null, "local", "/Repo", Ordinal, "main");

        await Assert.That(BaseRefMemory.Lookup(refs, "local", "/repo", StringComparer.OrdinalIgnoreCase)).IsEquivalentTo(new[] { "main" });
        await Assert.That(BaseRefMemory.Lookup(refs, "local", "/repo", Ordinal)).IsEmpty();
    }

    [Test]
    public async Task Forgetting_the_last_ref_drops_the_entry() {
        var refs = BaseRefMemory.Remember(null, "local", "/r", Ordinal, "main");

        refs = BaseRefMemory.Forget(refs, "local", "/r", Ordinal, "main");

        await Assert.That(refs.Count).IsEqualTo(0);
    }
}
