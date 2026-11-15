using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.App.Views.Onboarding;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit.Views.Onboarding;

public class OnboardingAccessibilityTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Composite_onboarding_links_expose_their_visible_labels_to_assistive_technology() {
        await AvaloniaSession.DispatchAsync(async () => {
            var done = new DoneStepViewModel(() => DoneFacts.Empty, addAgent: () => { });
            await done.OnEnterAsync(CancellationToken.None);
            var addAgent = new DonePaneView { DataContext = done }.FindControl<Button>("AddAgentButton")!;
            var docs = new HistoryPaneView().FindControl<Button>("RepoSettingsDocsButton")!;

            await Assert.That(ControlAutomationPeer.CreatePeerForElement(addAgent)!.GetName())
                .IsEqualTo("Add an agent");
            await Assert.That(ControlAutomationPeer.CreatePeerForElement(docs)!.GetName())
                .IsEqualTo("Repository settings in the docs");
        });
    }

    /// A choice whose content is a panel would otherwise be announced by that panel's type name.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Usage_data_choices_speak_their_titles() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var step = new UsageDataStepViewModel(null);
            var view = new OnboardingPaneView().DataTemplates.First(template => template.Match(step)).Build(step)!;
            view.DataContext = step;

            var choices = view.GetLogicalDescendants().OfType<RadioButton>()
                .Select(choice => ControlAutomationPeer.CreatePeerForElement(choice))
                .ToList();

            await Assert.That(choices.Select(peer => peer.GetName())).IsEquivalentTo(
                [UsageDataStepViewModel.ShareTitle, UsageDataStepViewModel.OffTitle], CollectionOrdering.Matching);
            await Assert.That(choices.Select(peer => peer.GetHelpText())).IsEquivalentTo(
                [UsageDataStepViewModel.ShareDetail, UsageDataStepViewModel.OffDetail], CollectionOrdering.Matching);
        });
    }

    /// The glyph is the only thing that says how far each install step has got, so the visible
    /// one carries that state as its name.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Install_rows_speak_the_state_their_icon_shows() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var row = new InstallRow();
            var item = new InstallStepRow("Start the local server", row);
            var view = new InstallPaneView().FindControl<ItemsControl>("InstallRows")!.ItemTemplate!.Build(item)!;
            view.DataContext = item;
            var window = new Window { Content = view };
            try {
                window.Show();
                var spoken = new List<string>();
                foreach (var state in new[] { RowState.Pending, RowState.Running, RowState.Done, RowState.Skipped }) {
                    row.State = state;
                    Dispatcher.UIThread.RunJobs();
                    spoken.AddRange(view.GetVisualDescendants().OfType<AppIcon>()
                        .Where(icon => icon.IsEffectivelyVisible)
                        .Select(icon => ControlAutomationPeer.CreatePeerForElement(icon))
                        .Where(peer => peer.IsControlElement())
                        .Select(peer => peer.GetName()));
                }

                await Assert.That(spoken).IsEquivalentTo(["Waiting", "In progress", "Done", "Skipped"], CollectionOrdering.Matching);
            } finally { window.Close(); }
        });
    }

    /// The link and Check again show only once the Ollama check answers, which on Windows takes long enough for a
    /// reader to walk the page while it still says it is looking; both must be reachable on the next walk.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_ollama_link_and_check_again_are_reachable_once_the_check_answers() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var answer = new TaskCompletionSource<bool>();
            var vm = new Capacitor.App.ViewModels.SearchChoiceViewModel(_ => answer.Task, _ => Task.FromResult(false));
            var window = new Window { Content = new SearchChoiceView { DataContext = vm }, Width = 640, Height = 800 };
            try {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var looking = ControlView.Descendants(window).Select(peer => peer.GetAutomationId()).ToList();

                answer.SetResult(false);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var answered = ControlView.Descendants(window);
                AutomationPeer Answered(string id) => answered.Single(peer => peer.GetAutomationId() == id);

                await Assert.That(looking).Contains("OllamaStatusText");
                await Assert.That(looking).DoesNotContain("CheckOllamaButton");
                await Assert.That(Answered("OllamaStatusText").GetName()).IsEqualTo("Ollama is not installed.");
                await Assert.That(Answered("OllamaSiteLink").GetName()).IsEqualTo("Get it free at ollama.com");
                await Assert.That(Answered("OllamaSiteLink").GetAutomationControlType()).IsEqualTo(AutomationControlType.Hyperlink);
                await Assert.That(Answered("CheckOllamaButton").GetName()).IsEqualTo("Check again");
            } finally { window.Close(); }
        });
    }
}
