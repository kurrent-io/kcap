using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_strip_shows_for_a_claude_session_with_an_account_and_not_from_an_older_daemon_or_another_vendor() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            try {
                await h.PushAsync(Dto(null) with { AccountDirectory = "/home/me/.claude-work", AccountId = "acc1", AccountLabel = "work" });
                await Assert.That(h.Chat.ShowsAccountStrip).IsTrue();
                await Assert.That(h.Chat.AccountVendor).IsEqualTo("claude");
                await Assert.That(h.Chat.AccountLabel).IsEqualTo("work");
                await Assert.That(h.Chat.AccountDirectory).IsEqualTo("/home/me/.claude-work");

                await h.PushAsync(Dto(null, "codex") with { AccountDirectory = "/home/me/.codex-work", AccountId = "acc2", AccountLabel = "side" });
                await Assert.That(h.Chat.ShowsAccountStrip).IsTrue();
                await Assert.That(h.Chat.AccountVendor).IsEqualTo("codex");

                await h.PushAsync(Dto(null));
                await Assert.That(h.Chat.ShowsAccountStrip).IsFalse();

                await h.PushAsync(Dto(null, "gemini") with { AccountDirectory = "/home/me/.gemini", AccountLabel = ".gemini" });
                await Assert.That(h.Chat.ShowsAccountStrip).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unregistered_default_shows_its_directory_as_the_label() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            try {
                await h.PushAsync(Dto(null, "codex") with { AccountDirectory = "/home/me/.codex", AccountLabel = ".codex" });
                await Assert.That(h.Chat.ShowsAccountStrip).IsTrue();
                await Assert.That(h.Chat.AccountLabel).IsEqualTo(".codex");
                await Assert.That(h.Chat.AccountDirectory).IsEqualTo("/home/me/.codex");

                await h.PushAsync(Dto(null, "codex") with { AccountDirectory = @"C:\Users\me\.codex\" });
                await Assert.That(h.Chat.ShowsAccountStrip).IsTrue();
                await Assert.That(h.Chat.AccountLabel).IsEqualTo(".codex");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_row_carries_the_account_onto_the_strip() {
        await RunOnUiAsync(async () => {
            var row = AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = "s1", Status = "running", DaemonName = "work-mac", OwnerUserId = "u1", Vendor = "codex",
                RegisteredAt = DateTime.UtcNow,
                AccountDirectory = "/Users/me/.codex-work", AccountId = "acc1", AccountLabel = "work",
            });
            using var session = new BehaviorSubject<ChatSessionInfo>(ChatSessionInfo.FromRemote(row, ended: false));
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
                new AcceptingChatInput(), new NoAttachmentUploader(), null, new RecordingOpener(), time,
                new FakePermissionService(), new SessionRuns(time));
            try {
                await Assert.That(chat.ShowsAccountStrip).IsTrue();
                await Assert.That(chat.AccountVendor).IsEqualTo("codex");
                await Assert.That(chat.AccountLabel).IsEqualTo("work");
                await Assert.That(chat.AccountDirectory).IsEqualTo("/Users/me/.codex-work");

                session.OnNext(ChatSessionInfo.FromRemote(row with { AccountDirectory = null, AccountId = null, AccountLabel = null }, ended: false));
                await Assert.That(chat.ShowsAccountStrip).IsFalse();
            } finally { await chat.TeardownAsync(); }
        });
    }

    sealed class StripRig {
        public BehaviorSubject<AccountsSnapshotDto?> Readings { get; } = new(null);
        public BehaviorSubject<DateTimeOffset> Clock { get; } = new(StripReadings.Now);
        public Harness H { get; }

        public StripRig() => H = new Harness(TranscriptChat.For("claude"), accountReadings: Readings, usageClock: Clock);

        public Task PushAsync(string? accountId = "acc1") =>
            H.PushAsync(Dto(null) with { AccountDirectory = StripReadings.Directory, AccountId = accountId, AccountLabel = "work" });

        public ChatTabViewModel Chat => H.Chat;
    }

    static async Task SettleAsync() {
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_shows_bars_for_a_drawable_reading() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                await rig.PushAsync();
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", StripReadings.Reading(
                    StripReadings.Window(300, 42, TimeSpan.FromMinutes(134)), StripReadings.Window(10080, 81, TimeSpan.FromDays(3))))));
                await WaitUntilAsync(() => rig.Chat.ShowsAccountUsage, what: "the bars");

                var usage = rig.Chat.AccountStripUsage!;
                await Assert.That(usage.Bars.Select(b => (b.Label, b.PercentText, b.Tone)))
                    .IsEquivalentTo(new[] { ("5h", "42%", UsageTone.Neutral), ("Week", "81%", UsageTone.Warning) }, CollectionOrdering.Matching);
                await Assert.That(usage.NextResetText).IsEqualTo("5h resets in 2h 14m");
                await Assert.That(rig.Chat.ShowsAccountDirectory).IsFalse();
                await Assert.That(rig.Chat.AccountReadingNote).IsNull();
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_shows_no_reading_yet_with_the_directory() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                await rig.PushAsync();
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", null)));
                await WaitUntilAsync(() => rig.Chat.AccountReadingNote is not null, what: "the note");

                await Assert.That(rig.Chat.AccountReadingNote).IsEqualTo("· no reading yet");
                await Assert.That(rig.Chat.ShowsAccountDirectory).IsTrue();
                await Assert.That(rig.Chat.ShowsAccountUsage).IsFalse();
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_shows_directory_only_when_the_host_is_not_drawable() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                await rig.PushAsync();
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", StripReadings.Reading(StripReadings.Window(300, 42, TimeSpan.FromHours(2))))));
                await WaitUntilAsync(() => rig.Chat.ShowsAccountUsage, what: "the bars");

                rig.Readings.OnNext(null);
                await WaitUntilAsync(() => !rig.Chat.ShowsAccountUsage, what: "the bars to go");

                await Assert.That(rig.Chat.AccountStripUsage).IsNull();
                await Assert.That(rig.Chat.ShowsAccountDirectory).IsTrue();
                await Assert.That(rig.Chat.AccountReadingNote).IsNull();
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_shows_directory_only_for_an_unregistered_default() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", StripReadings.Reading(StripReadings.Window(300, 42, TimeSpan.FromHours(2))))));
                await rig.PushAsync(accountId: null);
                await SettleAsync();

                await Assert.That(rig.Chat.ShowsAccountStrip).IsTrue();
                await Assert.That(rig.Chat.AccountStripUsage).IsNull();
                await Assert.That(rig.Chat.ShowsAccountDirectory).IsTrue();
                await Assert.That(rig.Chat.AccountReadingNote).IsNull();
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_shows_directory_only_for_an_account_missing_from_the_snapshot() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("other", StripReadings.Reading(StripReadings.Window(300, 42, TimeSpan.FromHours(2))))));
                await rig.PushAsync();
                await SettleAsync();

                await Assert.That(rig.Chat.AccountStripUsage).IsNull();
                await Assert.That(rig.Chat.ShowsAccountDirectory).IsTrue();
                await Assert.That(rig.Chat.AccountReadingNote).IsNull();
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_blocked_shows_limit_hit_with_its_reset() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                await rig.PushAsync();
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", StripReadings.Blocked(TimeSpan.FromMinutes(65),
                    StripReadings.Window(300, 100, TimeSpan.FromMinutes(65)), StripReadings.Window(10080, 81, TimeSpan.FromDays(3))))));
                await WaitUntilAsync(() => rig.Chat.ShowsAccountUsage, what: "the bars");

                var usage = rig.Chat.AccountStripUsage!;
                await Assert.That(usage.BlockedText).IsEqualTo("Limit hit · resets in 1h 05m");
                await Assert.That(usage.Bars[0].Tone).IsEqualTo(UsageTone.Danger);
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_follows_the_clock() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                await rig.PushAsync();
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", StripReadings.Reading(StripReadings.Window(300, 42, TimeSpan.FromMinutes(134).Add(TimeSpan.FromSeconds(30)))))));
                await WaitUntilAsync(() => rig.Chat.AccountStripUsage?.NextResetText == "5h resets in 2h 14m", what: "the first reset");
                var first = rig.Chat.AccountStripUsage;

                var changes = 0;
                using var watch = rig.Chat.WhenAnyValue(c => c.AccountStripUsage).Skip(1).Subscribe(_ => changes++);
                rig.Clock.OnNext(StripReadings.Now.AddSeconds(20));
                await SettleAsync();
                await Assert.That(changes).IsEqualTo(0);
                await Assert.That(rig.Chat.AccountStripUsage).IsSameReferenceAs(first);

                rig.Clock.OnNext(StripReadings.Now.AddMinutes(30));
                await WaitUntilAsync(() => rig.Chat.AccountStripUsage?.NextResetText == "5h resets in 1h 44m", what: "the clock's reset");
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    static AccountViewDto LabelledRow(string label, AccountReadingDto? reading) =>
        StripReadings.Account("a1", reading) with { Label = label };

    static AccountReadingDto ReadingAt(double used) =>
        StripReadings.Reading(StripReadings.Window(300, used, TimeSpan.FromHours(2)));

    static Task PushLaunchLabelAsync(Harness h) =>
        h.PushAsync(Dto(null) with { AccountDirectory = StripReadings.Directory, AccountId = "a1", AccountLabel = "old@x" });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Account_label_follows_the_snapshot_row() {
        await RunOnUiAsync(async () => {
            var readings = new BehaviorSubject<AccountsSnapshotDto?>(null);
            var h = new Harness(TranscriptChat.For("claude"), accountReadings: readings, usageClock: Observable.Return(StripReadings.Now));
            try {
                await PushLaunchLabelAsync(h);
                readings.OnNext(StripReadings.Snapshot(LabelledRow("new@x", ReadingAt(42))));
                await WaitUntilAsync(() => h.Chat.AccountLabel == "new@x", what: "the row's label");

                await Assert.That(h.Chat.AccountStripUsage).IsNotNull();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Account_label_and_usage_never_mix_when_the_row_changes() {
        await RunOnUiAsync(async () => {
            var readings = new BehaviorSubject<AccountsSnapshotDto?>(null);
            var h = new Harness(TranscriptChat.For("claude"), accountReadings: readings, usageClock: Observable.Return(StripReadings.Now));
            try {
                await PushLaunchLabelAsync(h);
                var seen = new List<(string? Label, string? Percent)>();
                h.Chat.PropertyChanged += (_, _) => seen.Add((h.Chat.AccountLabel, h.Chat.AccountStripUsage?.Bars[0].PercentText));

                readings.OnNext(StripReadings.Snapshot(LabelledRow("first@x", ReadingAt(42))));
                await WaitUntilAsync(() => h.Chat.AccountLabel == "first@x", what: "the first row");
                readings.OnNext(StripReadings.Snapshot(LabelledRow("second@x", ReadingAt(17))));
                await WaitUntilAsync(() => h.Chat.AccountLabel == "second@x", what: "the second row");
                readings.OnNext(StripReadings.Snapshot());
                await WaitUntilAsync(() => h.Chat.AccountLabel == "old@x", what: "the fallback");

                (string?, string?)[] whole = [("first@x", "42%"), ("second@x", "17%"), ("old@x", null)];
                await Assert.That(seen.Where(p => !whole.Contains(p))).IsEmpty();
                await Assert.That(seen.Distinct()).IsEquivalentTo(whole);
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Account_label_is_never_empty_beside_the_directory_while_the_row_catches_up() {
        await RunOnUiAsync(async () => {
            var readings = new BehaviorSubject<AccountsSnapshotDto?>(null);
            var h = new Harness(TranscriptChat.For("claude"), accountReadings: readings, usageClock: Observable.Return(StripReadings.Now));
            try {
                readings.OnNext(StripReadings.Snapshot(
                    LabelledRow("new@x", ReadingAt(42)),
                    StripReadings.Account("a2", ReadingAt(17)) with { Label = "second@x" }));
                var seen = new List<(string? Label, string? Percent)>();
                h.Chat.PropertyChanged += (_, _) => {
                    if (h.Chat.AccountDirectory is not null) seen.Add((h.Chat.AccountLabel, h.Chat.AccountStripUsage?.Bars[0].PercentText));
                };

                await PushLaunchLabelAsync(h);
                await WaitUntilAsync(() => h.Chat.AccountLabel == "new@x", what: "the row's label");
                await h.PushAsync(Dto(null) with { AccountDirectory = StripReadings.Directory, AccountId = "a2", AccountLabel = "other@x" });
                await WaitUntilAsync(() => h.Chat.AccountLabel == "second@x", what: "the second account's row");

                (string?, string?)[] whole = [("old@x", null), ("new@x", "42%"), ("other@x", null), ("second@x", "17%")];
                await Assert.That(seen.Where(p => !whole.Contains(p))).IsEmpty();
                await Assert.That(seen.Distinct()).IsEquivalentTo(whole);
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Same_account_relabel_keeps_the_bars() {
        await RunOnUiAsync(async () => {
            var readings = new BehaviorSubject<AccountsSnapshotDto?>(null);
            var h = new Harness(TranscriptChat.For("claude"), accountReadings: readings, usageClock: Observable.Return(StripReadings.Now));
            try {
                readings.OnNext(StripReadings.Snapshot(LabelledRow("mid@x", ReadingAt(42))));
                await PushLaunchLabelAsync(h);
                await WaitUntilAsync(() => h.Chat.AccountLabel == "mid@x", what: "the row's label");

                readings.OnNext(StripReadings.Snapshot(LabelledRow("new@x", ReadingAt(42))));
                await WaitUntilAsync(() => h.Chat.AccountLabel == "new@x", what: "the relabelled row");
                await h.PushAsync(Dto(null) with { AccountDirectory = StripReadings.Directory, AccountId = "a1", AccountLabel = "new@x" });
                await SettleAsync();

                await Assert.That(h.Chat.AccountStripUsage).IsNotNull();
                await Assert.That(h.Chat.AccountLabel).IsEqualTo("new@x");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stale_emission_for_the_previous_account_is_dropped() {
        await RunOnUiAsync(async () => {
            var readings = new BehaviorSubject<AccountsSnapshotDto?>(null);
            var h = new Harness(TranscriptChat.For("claude"), accountReadings: readings, usageClock: Observable.Return(StripReadings.Now));
            try {
                readings.OnNext(StripReadings.Snapshot(
                    LabelledRow("first@x", ReadingAt(42)),
                    StripReadings.Account("a2", ReadingAt(17)) with { Label = "second@x" }));
                await PushLaunchLabelAsync(h);
                await WaitUntilAsync(() => h.Chat.AccountLabel == "first@x", what: "the first account's row");

                var seen = new List<(string? Label, string? Percent)>();
                h.Chat.PropertyChanged += (_, _) => seen.Add((h.Chat.AccountLabel, h.Chat.AccountStripUsage?.Bars[0].PercentText));

                // A snapshot off the UI thread is computed for the account current at that moment and then
                // posted; the switch applies on the UI thread before that post runs.
                var emitted = Task.Run(() => readings.OnNext(StripReadings.Snapshot(
                    LabelledRow("first@x", ReadingAt(99)),
                    StripReadings.Account("a2", ReadingAt(17)) with { Label = "second@x" })));
                SpinWait.SpinUntil(() => emitted.IsCompleted);
                h.Daemon.Agents.AddOrUpdate(Dto(null) with { AccountDirectory = StripReadings.Directory, AccountId = "a2", AccountLabel = "other@x" });
                await WaitUntilAsync(() => h.Chat.AccountLabel == "second@x", what: "the second account's row");
                await SettleAsync();

                await Assert.That(seen.Where(p => p.Label == "first@x" || p.Percent is "99%" or "42%")).IsEmpty();
                await Assert.That(h.Chat.AccountLabel).IsEqualTo("second@x");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unchanged_account_pushes_leave_the_strip_alone() {
        await RunOnUiAsync(async () => {
            var readings = new BehaviorSubject<AccountsSnapshotDto?>(null);
            var h = new Harness(TranscriptChat.For("claude"), accountReadings: readings, usageClock: Observable.Return(StripReadings.Now));
            try {
                readings.OnNext(StripReadings.Snapshot(LabelledRow("new@x", ReadingAt(42))));
                await PushLaunchLabelAsync(h);
                await WaitUntilAsync(() => h.Chat.AccountLabel == "new@x", what: "the row's label");

                var changes = 0;
                h.Chat.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ChatTabViewModel.AccountStripUsage)) changes++; };
                await h.PushAsync(Dto(null) with { AccountDirectory = StripReadings.Directory, AccountId = "a1", AccountLabel = "old@x", Model = "other-model" });
                await SettleAsync();

                await Assert.That(changes).IsEqualTo(0);
                await Assert.That(h.Chat.AccountLabel).IsEqualTo("new@x");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Account_label_without_readings_source_is_the_launch_label() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"));
            try {
                await PushLaunchLabelAsync(h);

                await Assert.That(h.Chat.AccountLabel).IsEqualTo("old@x");
                await Assert.That(h.Chat.AccountStripUsage).IsNull();
            } finally { await h.TeardownAsync(); }
        });
    }
}
