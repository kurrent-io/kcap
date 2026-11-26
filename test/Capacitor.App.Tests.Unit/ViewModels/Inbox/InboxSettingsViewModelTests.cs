using RxUnit = System.Reactive.Unit;
using System.Reactive.Linq;
using Capacitor.App.Services.Inbox;
using Capacitor.App.ViewModels.Inbox;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit.ViewModels.Inbox;

[NotInParallel("AvaloniaSession")]
public class InboxSettingsViewModelTests {
    static NotificationTypePrefDto Type(string type, bool slackCapable = true, bool slack = false) =>
        new() { Type = type, DisplayName = type, Description = "d", Enabled = true, SlackEnabled = slack, SlackCapable = slackCapable };

    static InboxRead<NotificationPrefsDto> Prefs(string delivery, bool configured = true, params NotificationTypePrefDto[] types) =>
        new(InboxReadKind.Ready, new() { Types = types, Slack = new() { TenantConfigured = configured, Delivery = delivery } });

    [Test]
    [Arguments("connected", true, "Enabled types arrive as Slack direct messages", false)]
    [Arguments("needs_reconnect", true, "Slack delivery stopped. Reconnect Slack in Chat accounts on the kcap web app to resume", true)]
    [Arguments("not_connected", true, "Connect Slack in Chat accounts on the kcap web app to receive these in Slack", true)]
    [Arguments("not_connected", false, "Ask your admin to set up the Slack notifications app to receive these in Slack.", false)]
    public async Task The_slack_section_says_what_the_server_reports(string delivery, bool configured, string note, bool chatAccounts) =>
        await AvaloniaSession.RunOnUiAsync(async () => {
            var api = new FakeInboxApi { Prefs = Prefs(delivery, configured, Type("pass_received")) };
            using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: false, () => { });

            await Assert.That(settings.SlackNote).IsEqualTo(note);
            await Assert.That(settings.CanOpenChatAccounts).IsEqualTo(chatAccounts);
            await Assert.That(settings.Types.Single().SlackUsable).IsEqualTo(delivery == "connected");
        });

    [Test]
    public async Task Flipping_a_switch_writes_both_and_rereads_and_in_app_only_types_never_offer_slack() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi { Prefs = Prefs("connected", true, Type("pass_received"), Type("cli_update_available", slackCapable: false)) };
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: false, () => { });
        var reads = api.PrefsReads;

        settings.Types[0].SlackEnabled = true;

        await Assert.That(api.Calls).Contains("pref pass_received True True");
        await Assert.That(api.PrefsReads).IsEqualTo(reads + 1);
        await Assert.That(settings.Types[1].SlackUsable).IsFalse();
        await Assert.That(settings.Types[1].SlackName).IsEqualTo("Slack: not available for cli_update_available (in-app only)");
    });

    [Test]
    public async Task Pass_controls_show_only_where_passes_are_offered_and_write_through() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi {
            Prefs = Prefs("connected", true, Type("pass_received")),
            Controls = new(InboxReadKind.Ready, new() { AgentOptedOut = false, MutedSenders = [new() { UserId = "u9", Name = "Jordan Lee" }] }),
        };
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: true, () => { });

        await Assert.That(settings.TellAgent).IsTrue();
        await Assert.That(settings.MutedSenders.Single().Name).IsEqualTo("Jordan Lee");

        settings.TellAgent = false;
        settings.MutedSenders.Single().UnmuteCommand.Execute(null);

        await Assert.That(api.Calls).Contains("opt-out True");
        await Assert.That(api.Calls).Contains("unmute u9");
        await Assert.That(settings.HasMutedSenders).IsFalse();
    });

    [Test]
    public async Task Quick_flips_of_one_type_end_on_the_last_choice_in_order() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi { Prefs = Prefs("connected", true, Type("pass_received")) };
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: false, () => { });
        var gate = new TaskCompletionSource();
        api.PutGate = gate;

        settings.Types[0].Enabled = false;
        settings.Types[0].SlackEnabled = true;
        settings.Types[0].SlackEnabled = false;
        settings.Types[0].SlackEnabled = true;
        api.PutGate = null;
        gate.SetResult();
        await Task.Yield();

        await Assert.That(api.Calls.Where(c => c.StartsWith("pref", StringComparison.Ordinal)))
            .IsEquivalentTo(["pref pass_received False False", "pref pass_received False True"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    });

    [Test]
    public async Task A_read_begun_before_a_write_does_not_undo_it() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi {
            Prefs = Prefs("connected", true, Type("pass_received")),
            Controls = new(InboxReadKind.Ready, new() { AgentOptedOut = false }),
        };
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: true, () => { });
        var gate = new TaskCompletionSource();
        api.PrefsGate = gate;
        settings.Reload();

        settings.TellAgent = false;
        api.PrefsGate = null;
        gate.SetResult();
        await Task.Yield();

        await Assert.That(settings.TellAgent).IsFalse();
    });

    [Test]
    public async Task Failed_pass_controls_show_an_error_not_defaults() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi { Prefs = Prefs("connected", true, Type("pass_received")), Controls = InboxRead<PassControlsDto>.Failed };
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: true, () => { });

        await Assert.That(settings.IsLoaded).IsTrue();
        await Assert.That(settings.PassesLoaded).IsFalse();
        await Assert.That(settings.PassesError).IsEqualTo("Couldn't load your pass settings.");

        api.Controls = new(InboxReadKind.Ready, new() { AgentOptedOut = true });
        await settings.RetryCommand.Execute();
        await Assert.That(settings.PassesLoaded).IsTrue();
        await Assert.That(settings.TellAgent).IsFalse();
    });

    [Test]
    public async Task Signing_in_as_someone_else_drops_the_loaded_rows_and_a_signed_out_flip_writes_nothing() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi { Prefs = Prefs("connected", true, Type("pass_received")) };
        var status = new System.Reactive.Subjects.BehaviorSubject<Capacitor.Client.ServerLaneStatus>(new(Capacitor.Client.ServerLaneState.Connected, Subject: "alice"));
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: false, () => { }, status);
        var alicesRow = settings.Types.Single();

        status.OnNext(new(Capacitor.Client.ServerLaneState.SignedOut));
        await Assert.That(settings.CanWrite).IsFalse();
        alicesRow.Enabled = false;
        await Assert.That(api.Calls.Any(c => c.StartsWith("pref", StringComparison.Ordinal))).IsFalse();

        api.Prefs = Prefs("connected", true, Type("work_overlap"));
        status.OnNext(new(Capacitor.Client.ServerLaneState.Connected, Subject: "bob"));

        await Assert.That(settings.CanWrite).IsTrue();
        await Assert.That(settings.Types.Single().Type).IsEqualTo("work_overlap");
    });

    [Test]
    public async Task Quick_tell_agent_toggles_send_in_order_and_end_on_the_last_choice() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi { Prefs = Prefs("connected", true, Type("pass_received")), Controls = new(InboxReadKind.Ready, new() { AgentOptedOut = false }) };
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: true, () => { });
        var gate = new TaskCompletionSource();
        api.ControlsGate = gate;

        settings.TellAgent = false;
        settings.TellAgent = true;
        settings.TellAgent = false;
        api.ControlsGate = null;
        gate.SetResult();
        await Task.Yield();

        await Assert.That(api.Calls.Where(c => c.StartsWith("opt-out", StringComparison.Ordinal)))
            .IsEquivalentTo(["opt-out True", "opt-out True"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(settings.TellAgent).IsFalse();
    });

    [Test]
    public async Task A_pass_control_answer_from_before_a_sign_in_change_is_not_shown() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi { Prefs = Prefs("connected", true, Type("pass_received")), Controls = new(InboxReadKind.Ready, new() { AgentOptedOut = false }) };
        var status = new System.Reactive.Subjects.BehaviorSubject<Capacitor.Client.ServerLaneStatus>(new(Capacitor.Client.ServerLaneState.Connected, Subject: "alice"));
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: true, () => { }, status);
        var gate = new TaskCompletionSource();
        api.ControlsGate = gate;

        api.WritesChangeControls = false;
        settings.TellAgent = false;
        status.OnNext(new(Capacitor.Client.ServerLaneState.Connected, Subject: "bob"));
        api.ControlsGate = null;
        gate.SetResult();
        await Task.Yield();

        await Assert.That(settings.TellAgent).IsTrue();
    });

    [Test]
    public async Task Signing_out_during_the_first_load_stops_the_spinner() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi { Prefs = Prefs("connected", true, Type("pass_received")) };
        var gate = new TaskCompletionSource();
        api.PrefsGate = gate;
        var status = new System.Reactive.Subjects.BehaviorSubject<Capacitor.Client.ServerLaneStatus>(new(Capacitor.Client.ServerLaneState.Connected, Subject: "alice"));
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: false, () => { }, status);
        await Assert.That(settings.IsLoading).IsTrue();

        status.OnNext(new(Capacitor.Client.ServerLaneState.SignedOut));

        await Assert.That(settings.IsLoading).IsFalse();
        gate.SetResult();
    });

    [Test]
    public async Task A_completed_sign_in_closes_writes_until_that_accounts_settings_load() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi { Prefs = Prefs("connected", true, Type("pass_received")) };
        var signIn = new System.Reactive.Subjects.Subject<RxUnit>();
        var status = new System.Reactive.Subjects.BehaviorSubject<Capacitor.Client.ServerLaneStatus>(new(Capacitor.Client.ServerLaneState.Connected, Subject: "alice"));
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: false, () => { }, status, signIn);
        var row = settings.Types.Single();
        var gate = new TaskCompletionSource();
        api.PrefsGate = gate;

        signIn.OnNext(RxUnit.Default);
        row.Enabled = false;

        await Assert.That(settings.CanWrite).IsFalse();
        await Assert.That(api.Calls.Any(c => c.StartsWith("pref", StringComparison.Ordinal))).IsFalse();
        api.PrefsGate = null;
        gate.SetResult();
        await Task.Yield();
        await Assert.That(settings.CanWrite).IsTrue();
    });

    [Test]
    public async Task Switches_keep_the_latest_choices_while_queued_writes_drain() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi { Prefs = Prefs("connected", true, Type("pass_received")) };
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: false, () => { });
        var row = settings.Types.Single();
        var first = new TaskCompletionSource();
        api.PutGate = first;
        row.Enabled = false;
        row.SlackEnabled = true;

        var second = new TaskCompletionSource();
        api.PutGate = second;
        first.SetResult();
        await Task.Yield();

        await Assert.That(row.Enabled).IsFalse();
        await Assert.That(row.SlackEnabled).IsTrue();
        await Assert.That(api.PrefsReads).IsEqualTo(1);
        api.PutGate = null;
        second.SetResult();
    });

    [Test]
    public async Task An_older_pass_control_answer_landing_last_is_not_shown() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi {
            Prefs = Prefs("connected", true, Type("pass_received")),
            Controls = new(InboxReadKind.Ready, new() { AgentOptedOut = false, MutedSenders = [new() { UserId = "u9", Name = "Jordan Lee" }] }),
        };
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: true, () => { });
        var unmuteGate = new TaskCompletionSource();
        api.ControlsGate = unmuteGate;
        settings.MutedSenders.Single().UnmuteCommand.Execute(null);

        var toggleGate = new TaskCompletionSource();
        api.ControlsGate = toggleGate;
        settings.TellAgent = false;
        api.ControlsGate = null;
        toggleGate.SetResult();
        await Task.Yield();
        // The reload after the writes fails, so only the answers decide what the switch shows.
        api.Prefs = InboxRead<NotificationPrefsDto>.Failed;
        unmuteGate.SetResult();
        await Task.Yield();

        await Assert.That(settings.TellAgent).IsFalse();
    });

    [Test]
    public async Task A_stalled_write_from_the_previous_account_does_not_hold_back_the_next() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi { Prefs = Prefs("connected", true, Type("pass_received")) };
        var signIn = new System.Reactive.Subjects.Subject<RxUnit>();
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: false, () => { }, null, signIn);
        var stalled = new TaskCompletionSource();
        api.PutGate = stalled;
        settings.Types.Single().Enabled = false;
        api.PutGate = null;

        signIn.OnNext(RxUnit.Default);
        await Task.Yield();

        await Assert.That(settings.CanWrite).IsTrue();
        await Assert.That(api.PrefsReads).IsEqualTo(2);
        stalled.SetResult();
    });

    [Test]
    public async Task A_failed_first_load_offers_a_retry() => await AvaloniaSession.RunOnUiAsync(async () => {
        var api = new FakeInboxApi { Prefs = InboxRead<NotificationPrefsDto>.Failed };
        using var settings = new InboxSettingsViewModel(api, Observable.Never<RxUnit>(), passesOffered: false, () => { });
        await Assert.That(settings.LoadError).IsEqualTo("Couldn't load your notification preferences.");

        api.Prefs = Prefs("connected", true, Type("pass_received"));
        await settings.RetryCommand.Execute();
        await Assert.That(settings.LoadError).IsNull();
        await Assert.That(settings.IsLoaded).IsTrue();
    });
}
