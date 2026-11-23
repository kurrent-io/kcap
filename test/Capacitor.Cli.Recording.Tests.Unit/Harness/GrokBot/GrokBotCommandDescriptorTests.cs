using System.Text.Json.Nodes;
using Capacitor.Cli.Recording.Harness.GrokBot;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.GrokBot;

/// <summary>The gateway descriptor read from a command's output, for a cloud computer that leaves the
/// descriptor unreadable to a Bot's own user. The command's output holds the bearer token, so a failure must
/// not put it in the thrown message.</summary>
public class GrokBotCommandDescriptorTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task An_empty_command_is_refused() {
        var descriptor = new GrokBotCommandDescriptor("   ", new FakeTimeProvider());

        await Assert.That(descriptor.ReadJson).Throws<InvalidOperationException>();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task The_command_output_is_the_descriptor() {
        var stub = Tmp.CreateExecutable("gw", """
            #!/bin/sh
            echo '{"scheme":"http","host":"127.0.0.1","port":1340,"token":"t"}'
            """);

        var json = new GrokBotCommandDescriptor(stub, new FakeTimeProvider()).ReadJson();

        await Assert.That(JsonNode.Parse(json)!["port"]!.GetValue<int>()).IsEqualTo(1340);
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_failing_command_keeps_its_output_out_of_the_error() {
        var stub = Tmp.CreateExecutable("gw", """
            #!/bin/sh
            echo '{"token":"leak-me"}'
            echo 'a password is required' 1>&2
            exit 7
            """);

        InvalidOperationException? caught = null;
        try { new GrokBotCommandDescriptor(stub, new FakeTimeProvider()).ReadJson(); }
        catch (InvalidOperationException ex) { caught = ex; }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("exited 7");
        await Assert.That(caught.Message).Contains("a password is required");
        await Assert.That(caught.Message).DoesNotContain("leak-me");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task The_descriptor_is_cached_then_re_read_after_a_rotation() {
        var runs = Tmp.PathTo("runs");
        var stub = Tmp.CreateExecutable("gw", $$"""
            #!/bin/sh
            echo x >> "{{runs}}"
            echo '{"scheme":"http","host":"127.0.0.1","port":1340,"token":"t"}'
            """);

        var time       = new FakeTimeProvider();
        var descriptor = new GrokBotCommandDescriptor(stub, time);

        descriptor.ReadJson();
        descriptor.ReadJson();
        await Assert.That(File.ReadAllLines(runs).Length).IsEqualTo(1);

        time.Advance(TimeSpan.FromSeconds(6));
        descriptor.ReadJson();
        await Assert.That(File.ReadAllLines(runs).Length).IsEqualTo(2);
    }
}
