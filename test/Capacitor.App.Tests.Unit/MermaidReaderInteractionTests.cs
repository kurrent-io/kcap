using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class MermaidReaderInteractionTests {
    [Test]
    [Arguments("https://github.com/example/repo/pull/1#issuecomment-123", true)]
    [Arguments("javascript:alert(1)", false)]
    public Task The_actual_reader_routes_diagrams_to_the_original_PR_or_comment_under_its_access_guard(string commentUrl, bool allowed) => RunOnUiAsync(async () => {
        const string diagram = "```mermaid\ngraph TD\n A-->B\n click A \"https://untrusted.example/ignored\"\n```";
        var time = new FakeTimeProvider();
        var source = new FakePullRequestSource(time) { Links = [FakePullRequestSource.Link(1)], TotalPages = 1 };
        source.OverviewResponses.Enqueue((subject, _) => {
            var read = source.Overview(subject);
            return Task.FromResult(read with { Data = read.Data! with { Description = diagram } });
        });
        source.PageItem = _ => new PullRequestCommentDto { Id = "comment", Availability = "available", Body = diagram, Url = commentUrl };
        using var presence = new BehaviorSubject<AgentStatusDto?>(Agent("agent", "claude", false, sessionId: "session"));
        var opener = new RecordingOpener();
        var model = new PullRequestContextViewModel(presence, source, time, opener, () => { });
        var reader = new PullRequestReader { DataContext = model };
        var window = new Window { Content = reader, Width = 700, Height = 700 };
        window.Show();
        try {
            model.SetForeground(true);
            model.SetReaderVisible(true);
            await WaitUntilAsync(() => model.CanReveal && !model.IsReading, what: "PR diagram loaded");
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            await Assert.That(opener.Opened).IsEmpty();
            var overview = All<MermaidFallback>(reader).Single(f => f.IsEffectivelyVisible);
            ClickCentre(window, All<Button>(overview).Single(b => b.Classes.Contains("markdown-mermaid-open")));
            await Assert.That(opener.Opened).IsEquivalentTo(new[] { "https://github.com/example/repo/pull/1" });
            await model.ShowSectionCommand.Execute("conversation");
            await WaitUntilAsync(() => model.DiscussionRows.Count == 1 && !model.IsReading, what: "comment diagram loaded");
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var comment = All<MermaidFallback>(reader).Single(f => f.IsEffectivelyVisible);
            var open = All<Button>(comment).Single(b => b.Classes.Contains("markdown-mermaid-open"));
            await Assert.That(open.IsVisible).IsEqualTo(allowed);
            if (allowed) {
                ClickCentre(window, open);
                await Assert.That(opener.Opened.Last()).IsEqualTo(commentUrl);
                var queued = open.Command!;
                var parameter = open.CommandParameter;
                model.SetForeground(false);
                Dispatcher.UIThread.RunJobs();
                queued.Execute(parameter);
                await Assert.That(opener.Opened).Count().IsEqualTo(2);
            } else {
                await Assert.That(opener.Opened).Count().IsEqualTo(1);
            }
        } finally { window.Close(); await model.TeardownAsync(); }
    });
}
