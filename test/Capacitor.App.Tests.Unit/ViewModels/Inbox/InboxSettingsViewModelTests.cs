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
