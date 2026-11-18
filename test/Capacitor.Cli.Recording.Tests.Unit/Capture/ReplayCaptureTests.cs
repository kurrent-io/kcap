using Capacitor.Cli.Core.Transcripts;
using Capacitor.Cli.Recording.Capture;

namespace Capacitor.Cli.Recording.Tests.Unit.Capture;

public class ReplayCaptureTests {
    static RedactionOutcome Outcome(RedactionLossReason? loss) => new("{}", loss, 2, 2);

    [Test]
    [Arguments(null, LineCapture.Kept)]
    [Arguments(RedactionLossReason.OutputLimit, LineCapture.Lost)]
    [Arguments(RedactionLossReason.MalformedInput, LineCapture.Lost)]
    [Arguments(RedactionLossReason.RegexTimeout, LineCapture.Undecided)]
    [Arguments(RedactionLossReason.RecordBudget, LineCapture.Undecided)]
    public async Task A_line_is_what_the_producers_redaction_made_of_it(RedactionLossReason? loss, LineCapture capture) {
        using var replay = new CancellationTokenSource();

        await Assert.That(ReplayCapture.With(_ => Outcome(loss))("{}", replay.Token)).IsEqualTo(capture);
        await Assert.That(ReplayCapture.With(_ => Outcome(loss))("{}", CancellationToken.None)).IsEqualTo(capture);
    }

    /// <summary>The replay's own time ends the wait for a long redaction, which is left to finish by itself.</summary>
    [Test]
    public async Task A_redaction_still_running_when_the_replay_runs_out_of_time_leaves_the_line_undecided() {
        var       started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var       release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var replay  = new CancellationTokenSource();
        var decide = ReplayCapture.With(_ => {
            started.TrySetResult();
            release.Task.Wait();

            return Outcome(null);
        });

        try {
            var deciding = Task.Run(() => decide("{}", replay.Token));
            await started.Task.WithinDeadlineAsync("the redaction to start");
            await replay.CancelAsync();

            await Assert.That(await deciding.WithinDeadlineAsync("the decision")).IsEqualTo(LineCapture.Undecided);
        } finally {
            release.TrySetResult();
        }
    }

    /// <summary>The redaction ran out of its own budget during the replay: nothing is held or retried, and the source goes
    /// raw.</summary>
    [Test]
    public async Task A_replay_whose_redaction_runs_out_of_its_budget_is_incomplete() {
        using var tmp = new TempDir();
        var large = $$$"""{"type":"user","uuid":"40000000-0000-4000-8000-000000000001","message":{"role":"user","content":"{{{new string('x', CaptureRule.MaxRecordBytes / CaptureRule.MaxRewriteGrowth)}}}"}}""";
        var path  = tmp.CreateFile("t.jsonl", $"{large}\n{{\"type\":\"user\"}}\n");
        var route = CanonicalRoute.For("claude", "s", null, true, ReplayCapture.With(line =>
            SecretRedactor.RedactLineWithOutcome(line, new RedactionBudget(TimeProvider.System, TimeSpan.Zero), SecretRedactor.WatcherPatterns)))!;

        await Assert.That(await route.ReplayAsync(path, 2, CancellationToken.None)).IsFalse();
        await Assert.That(route.Active).IsTrue();
    }
}
