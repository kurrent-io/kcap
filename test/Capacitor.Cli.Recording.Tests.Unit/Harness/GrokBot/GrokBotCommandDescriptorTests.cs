using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.Harness.GrokBot;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.GrokBot;

/// <summary>The gateway descriptor read from a command's output, for a cloud computer that leaves the
/// descriptor unreadable to a Bot's own user. The command's output holds the bearer token, so a failure must
/// not put it in the thrown message.</summary>
public class GrokBotCommandDescriptorTests {
    sealed class StubRunner(ProcessResult result) : IProcessRunner {
        public int Runs { get; private set; }
        public string? File { get; private set; }
        public string[] Args { get; private set; } = [];

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Runs++;
            File = fileName;
            Args = args;
            return Task.FromResult(result);
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    static StubRunner Serving(string json) => new(new ProcessResult(0, json, "", TimedOut: false));

    [Test]
    public async Task An_empty_command_is_refused() {
        var descriptor = new GrokBotCommandDescriptor("   ", Serving("{}"), new FakeTimeProvider());

        await Assert.That(async () => await descriptor.ReadJsonAsync(CancellationToken.None)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task The_command_is_run_directly_with_its_arguments() {
        var runner     = Serving("""{"scheme":"http","host":"127.0.0.1","port":1340,"token":"t"}""");
        var descriptor = new GrokBotCommandDescriptor("sudo -n cat /home/box/gateway.json", runner, new FakeTimeProvider());

        var json = await descriptor.ReadJsonAsync(CancellationToken.None);

        await Assert.That(JsonNode.Parse(json)!["port"]!.GetValue<int>()).IsEqualTo(1340);
        await Assert.That(runner.File).IsEqualTo("sudo");
        await Assert.That(runner.Args).IsEquivalentTo(["-n", "cat", "/home/box/gateway.json"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_failing_command_keeps_its_output_out_of_the_error() {
        var runner     = new StubRunner(new ProcessResult(7, """{"token":"leak-me"}""", "a password is required", TimedOut: false));
        var descriptor = new GrokBotCommandDescriptor("sudo -n cat /x", runner, new FakeTimeProvider());

        InvalidOperationException? caught = null;
        try { await descriptor.ReadJsonAsync(CancellationToken.None); }
        catch (InvalidOperationException ex) { caught = ex; }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("exited 7");
        await Assert.That(caught.Message).Contains("a password is required");
        await Assert.That(caught.Message).DoesNotContain("leak-me");
    }

    [Test]
    public async Task A_command_that_outruns_the_deadline_is_a_failure() {
        var runner     = new StubRunner(new ProcessResult(-1, "", "", TimedOut: true));
        var descriptor = new GrokBotCommandDescriptor("sudo -n cat /x", runner, new FakeTimeProvider());

        await Assert.That(async () => await descriptor.ReadJsonAsync(CancellationToken.None))
            .Throws<InvalidOperationException>().WithMessageContaining("within 15s");
    }

    [Test]
    public async Task The_descriptor_is_cached_then_re_read_after_a_rotation() {
        var runner     = Serving("""{"scheme":"http","host":"127.0.0.1","port":1340,"token":"t"}""");
        var time       = new FakeTimeProvider();
        var descriptor = new GrokBotCommandDescriptor("sudo -n cat /x", runner, time);

        await descriptor.ReadJsonAsync(CancellationToken.None);
        await descriptor.ReadJsonAsync(CancellationToken.None);
        await Assert.That(runner.Runs).IsEqualTo(1);

        time.Advance(TimeSpan.FromSeconds(6));
        await descriptor.ReadJsonAsync(CancellationToken.None);
        await Assert.That(runner.Runs).IsEqualTo(2);
    }
}
