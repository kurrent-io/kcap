using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class VendorModelCatalogsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static Dictionary<string, VendorModelOption[]> Cat(params (string Vendor, VendorModelOption[] Models)[] entries) =>
        entries.ToDictionary(e => e.Vendor, e => e.Models, StringComparer.Ordinal);

    static readonly VendorModelOption A = new("p/a", "A · p");
    static readonly VendorModelOption B = new("p/b", "B · p");

    [Test]
    public async Task Equal_is_true_for_same_keys_and_ordered_pairs_from_fresh_objects() {
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A, B])), Cat(("pi", [new("p/a", "A · p"), new("p/b", "B · p")])))).IsTrue();
        await Assert.That(VendorModelCatalogs.Equal(null, null)).IsTrue();
        await Assert.That(VendorModelCatalogs.Equal(Cat(), Cat())).IsTrue();
    }

    [Test]
    public async Task Equal_is_false_for_reorder_relabel_extra_key_or_null_vs_empty() {
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A, B])), Cat(("pi", [B, A])))).IsFalse();
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A])), Cat(("pi", [new("p/a", "other")])))).IsFalse();
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A])), Cat(("pi", [A]), ("kiro", [])))).IsFalse();
        await Assert.That(VendorModelCatalogs.Equal(Cat(("pi", [A])), Cat(("kiro", [A])))).IsFalse();
        await Assert.That(VendorModelCatalogs.Equal(null, Cat())).IsFalse();
    }

    [Test]
    public async Task Merge_takes_fresh_answers_keeps_previous_on_null_reprobe_and_returns_a_new_instance() {
        var previous = Cat(("pi", [A]), ("other", [B]));
        var fresh    = Cat(("pi", [A, B]));            // "other" was probed and answered null

        var merged = VendorModelCatalogs.Merge(previous, fresh);

        await Assert.That(ReferenceEquals(merged, previous)).IsFalse();
        await Assert.That(merged["pi"]).IsEquivalentTo([A, B], CollectionOrdering.Matching);
        await Assert.That(merged["other"]).IsEquivalentTo([B]);
        await Assert.That(previous["pi"]).IsEquivalentTo([A]);
    }

    [Test]
    public async Task Merge_over_no_previous_is_the_fresh_answers() {
        var merged = VendorModelCatalogs.Merge(null, Cat(("pi", [])));
        await Assert.That(merged["pi"]).IsEmpty();
        await Assert.That(merged.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Probe_folds_only_non_null_answers_keyed_by_vendor() {
        var skipped = new StubCatalogFactory("codex", [B]);
        IHostedAgentRuntimeFactory[] factories = [
            new StubCatalogFactory("pi", [A]),
            new StubCatalogFactory("kiro", []),
            new StubCatalogFactory("claude", null),
            skipped,
        ];

        var result = await VendorModelCatalogs.ProbeAsync(factories, ["pi", "kiro", "claude"], CancellationToken.None);

        await Assert.That(result.Keys).IsEquivalentTo(["pi", "kiro"]);
        await Assert.That(result["kiro"]).IsEmpty();
        await Assert.That(skipped.ProbeCalls).IsEqualTo(0);
    }

    static Task<IReadOnlyList<VendorModelOption>?> Never() =>
        new TaskCompletionSource<IReadOnlyList<VendorModelOption>?>().Task;

    /// A probe whose teardown never ends answers null at the ceiling and is named; the vendors that
    /// answered keep their catalogs.
    [Test]
    public async Task Probe_past_the_ceiling_answers_null_and_names_the_vendor() {
        var time    = new FakeTimeProvider();
        var overran = new ConcurrentQueue<string>();
        IHostedAgentRuntimeFactory[] factories = [
            new StubCatalogFactory("pi", [A]),
            new StubCatalogFactory("codex", [B]) { Probe = Never },
        ];

        var probing = VendorModelCatalogs.ProbeAsync(
            factories, ["pi", "codex"], CancellationToken.None, new ProbeCeiling(TimeSpan.FromSeconds(30), time, overran.Enqueue));
        time.Advance(TimeSpan.FromSeconds(30));
        var result = await probing.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(result.Keys).IsEquivalentTo(["pi"]);
        await Assert.That(overran).IsEquivalentTo(["codex"]);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_probe_past_the_ceiling_that_answers_later_is_named_only_when_it_has_a_catalog(bool hasCatalog) {
        var time  = new FakeTimeProvider();
        var late  = new ConcurrentQueue<string>();
        var stuck = new TaskCompletionSource<IReadOnlyList<VendorModelOption>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IHostedAgentRuntimeFactory[] factories = [new StubCatalogFactory("codex", [B]) { Probe = () => stuck.Task }];

        var probing = VendorModelCatalogs.ProbeAsync(
            factories, ["codex"], CancellationToken.None, new ProbeCeiling(TimeSpan.FromSeconds(30), time, _ => { }, late.Enqueue));
        time.Advance(TimeSpan.FromSeconds(30));
        await probing.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(late).IsEmpty();

        stuck.SetResult(hasCatalog ? [B] : null);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (hasCatalog && late.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(10);
        if (!hasCatalog) await Task.Delay(100);

        string[] expected = hasCatalog ? ["codex"] : [];
        await Assert.That(late.ToArray()).IsEquivalentTo(expected);
    }

    [Test]
    public async Task An_account_probe_past_the_ceiling_is_named_by_its_account_key() {
        var time    = new FakeTimeProvider();
        var overran = new ConcurrentQueue<string>();
        var codex   = new StubCatalogFactory("codex", [B]) { ProbeDirectory = d => d == "/stuck" ? Never() : Task.FromResult<IReadOnlyList<VendorModelOption>?>([A]) };

        var probing = VendorModelCatalogs.ProbeByAccountAsync(
            codex, [("acct-stuck", "/stuck"), ("acct-ok", "/ok")], CancellationToken.None,
            new ProbeCeiling(TimeSpan.FromSeconds(30), time, overran.Enqueue));
        time.Advance(TimeSpan.FromSeconds(30));
        var result = await probing.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(result.Keys).IsEquivalentTo(["acct-ok"]);
        await Assert.That(overran).IsEquivalentTo([VendorModelCatalogs.AccountKey("acct-stuck")]);
    }

    [Test]
    public async Task Fingerprint_records_an_empty_array_for_a_factory_with_no_paths() {
        var fp = VendorModelCatalogs.FingerprintCatalogPaths([new StubCatalogFactory("claude", null)], ["claude"]);
        await Assert.That(fp["claude"]).IsEmpty();
    }

    [Test]
    public async Task CatalogPathStat_distinguishes_missing_from_present() {
        var path    = Tmp.PathTo("auth.json");
        var missing = CatalogPathStat.Of(path);
        Tmp.CreateFile("auth.json", "{}");
        var present = CatalogPathStat.Of(path);

        await Assert.That(missing.Exists).IsFalse();
        await Assert.That(present.Exists).IsTrue();
        await Assert.That(missing).IsNotEqualTo(present);
    }

    /// A file whose size and write time match the previous fingerprint is not read again: the
    /// previous digest, here one no content could hash to, comes back unchanged.
    [Test]
    public async Task CatalogPathStat_reuses_the_previous_digest_for_an_untouched_file() {
        var path     = Tmp.CreateFile("models_cache.json", "{}");
        var first    = CatalogPathStat.Of(path);
        var previous = first with { Digest = "untouched" };

        await Assert.That(CatalogPathStat.Of(path, previous).Digest).IsEqualTo("untouched");
        await Assert.That(CatalogPathStat.Of(path, previous with { Length = first.Length + 1 }).Digest).IsEqualTo(first.Digest);
    }
}
