using Capacitor.App.Services;
using Capacitor.App.Services.Voice;

namespace Capacitor.App.Tests.Unit.Services.Voice;

public class VoiceCommandExecutorTests {
    static readonly List<VoiceSession> TwoSessions = [
        new("local:a", "Fix login redirect loop", "Working", false),
        new("local:b", "Add uploader retries", "Idle", false),
    ];

    [Test]
    public async Task A_prompt_goes_to_the_open_session_and_watches_its_turn() {
        var host = new FakeVoiceHost { SessionList = TwoSessions, OpenId = "local:b" };

        var response = await new VoiceCommandExecutor(host).ExecuteAsync(new VoiceCommand.Prompt("run the tests"));

        await Assert.That(host.Calls).Contains("send run the tests");
        await Assert.That(response.Speech).IsEqualTo("Sent to Add uploader retries.");
        await Assert.That(response.WatchTurn).IsTrue();
    }

    [Test]
    public async Task A_prompt_with_no_session_open_starts_one() {
        var host = new FakeVoiceHost();

        var response = await new VoiceCommandExecutor(host).ExecuteAsync(new VoiceCommand.Prompt("write a changelog"));

        await Assert.That(host.Calls).Contains("start write a changelog");
        await Assert.That(response.Speech).StartsWith("Starting a session.");
    }

    [Test]
    public async Task A_refused_send_is_reported_as_a_failure() {
        var host = new FakeVoiceHost { SessionList = TwoSessions, OpenId = "local:a", SendRefusal = "The agent is busy." };

        var response = await new VoiceCommandExecutor(host).ExecuteAsync(new VoiceCommand.Prompt("hello"));

        await Assert.That(response.Failed).IsTrue();
        await Assert.That(response.Speech).IsEqualTo("The agent is busy.");
        await Assert.That(response.WatchTurn).IsFalse();
    }

    [Test]
    public async Task Stopping_the_agent_waits_for_a_yes() {
        var host = new FakeVoiceHost { SessionList = TwoSessions, OpenId = "local:a" };

        var response = await new VoiceCommandExecutor(host).ExecuteAsync(new VoiceCommand.StopAgent());

        await Assert.That(host.Calls).DoesNotContain("stop");
        await Assert.That(response.FollowUp).IsEqualTo(VoiceFollowUp.Confirm);
        await response.OnConfirm!();
        await Assert.That(host.Calls).Contains("stop");
    }

    [Test]
    public async Task Sessions_open_by_a_spoken_part_of_their_title() {
        var host = new FakeVoiceHost { SessionList = TwoSessions };

        var response = await new VoiceCommandExecutor(host).ExecuteAsync(new VoiceCommand.OpenSession("the login one"));

        await Assert.That(host.Calls).Contains("open local:a");
        await Assert.That(response.Speech).IsEqualTo("Opened Fix login redirect loop.");
    }

    [Test]
    public async Task Next_session_wraps_round_the_rail() {
        var host = new FakeVoiceHost { SessionList = TwoSessions, OpenId = "local:b" };

        await new VoiceCommandExecutor(host).ExecuteAsync(new VoiceCommand.NextSession());

        await Assert.That(host.OpenId).IsEqualTo("local:a");
    }

    [Test]
    public async Task A_permission_answer_with_nothing_waiting_fails_without_answering() {
        var host = new FakeVoiceHost();

        var response = await new VoiceCommandExecutor(host).ExecuteAsync(new VoiceCommand.PermissionReply(PermissionAnswer.Allow));

        await Assert.That(response.Failed).IsTrue();
        await Assert.That(host.Calls.Any(c => c.StartsWith("permission", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Allowing_resumes_watching_the_turn() {
        var host = new FakeVoiceHost { Permission = "Bash: npm test" };

        var response = await new VoiceCommandExecutor(host).ExecuteAsync(new VoiceCommand.PermissionReply(PermissionAnswer.Allow));

        await Assert.That(host.Calls).Contains("permission Allow");
        await Assert.That(response.WatchTurn).IsTrue();
    }

    [Test]
    public async Task The_last_reply_is_read_without_its_markdown() {
        var response = await new VoiceCommandExecutor(new FakeVoiceHost()).ExecuteAsync(new VoiceCommand.ReadReply());
        await Assert.That(response.Speech).IsEqualTo("Done. All tests pass.");
    }

    [Test]
    public async Task Continue_with_no_reply_being_read_is_a_prompt_for_the_agent() {
        var host = new FakeVoiceHost { SessionList = TwoSessions, OpenId = "local:a" };

        await new VoiceCommandExecutor(host).ExecuteAsync(new VoiceCommand.ReadMore("Continue."));

        await Assert.That(host.Calls).Contains("send Continue.");
    }

    [Test]
    public async Task Continue_part_way_through_a_long_reply_reads_on() {
        var host = new FakeVoiceHost { SessionList = TwoSessions, OpenId = "local:a" };
        var executor = new VoiceCommandExecutor(host);
        var opened = executor.OpenReply(string.Join("\n\n", Enumerable.Range(1, 5).Select(i => $"## Part {i}\n" + new string('x', 30) + " " + string.Join(" ", Enumerable.Repeat("More words here.", 8)))));
        await Assert.That(opened.OpensReply).IsTrue();

        var next = await executor.ExecuteAsync(new VoiceCommand.ReadMore("continue"));

        await Assert.That(next.Speech).StartsWith("Part 1.");
        await Assert.That(host.Calls.Any(c => c.StartsWith("send", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task A_failed_step_stops_the_rest_so_the_wrong_session_is_never_stopped() {
        var host = new FakeVoiceHost { SessionList = TwoSessions, OpenId = "local:a" };
        var executor = new VoiceCommandExecutor(host);

        var response = await executor.ExecuteAllAsync([new VoiceCommand.OpenSession("billing"), new VoiceCommand.StopAgent()]);

        await Assert.That(response.Failed).IsTrue();
        await Assert.That(response.Speech).IsEqualTo("I couldn't find a session matching billing.");
        await Assert.That(response.OnConfirm is null).IsTrue();
    }

    [Test]
    public async Task Replies_and_lists_are_read_while_answers_are_not() {
        var host = new FakeVoiceHost { SessionList = TwoSessions, OpenId = "local:a" };
        var executor = new VoiceCommandExecutor(host);

        await Assert.That((await executor.ExecuteAsync(new VoiceCommand.ReadReply())).Reads).IsTrue();
        await Assert.That((await executor.ExecuteAsync(new VoiceCommand.ListSessions())).Reads).IsTrue();
        await Assert.That((await executor.ExecuteAsync(new VoiceCommand.Help())).Reads).IsTrue();
        await Assert.That((await executor.ExecuteAsync(new VoiceCommand.Prompt("hi"))).Reads).IsFalse();
        await Assert.That((await executor.ExecuteAsync(new VoiceCommand.GoHome())).Reads).IsFalse();
    }

    [Test]
    public async Task Stop_listening_does_not_raise_the_window() {
        var host = new FakeVoiceHost();

        var response = await new VoiceCommandExecutor(host).ExecuteAsync(new VoiceCommand.StopListening());

        await Assert.That(response.FollowUp).IsEqualTo(VoiceFollowUp.StopListening);
        await Assert.That(host.Calls).DoesNotContain("show");
    }
}
