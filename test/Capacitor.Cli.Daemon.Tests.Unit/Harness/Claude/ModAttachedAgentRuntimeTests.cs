using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// <summary>One attached session's runtime: no terminal, no keys, no stop of its own, and what it holds for the mod.</summary>
public class ModAttachedAgentRuntimeTests {
    const string Session = "0f8fad5b-d9cb-469f-a165-70867728950e";
    const string Key     = "4242:Sat Nov  8 10:00:00 2026";

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));

    static string AgentOf(string session) => ModAttachments.AgentIdFor(SessionIds.Canonical(session)!);

    [Test]
    public async Task The_runtime_offers_no_terminal_keys_or_resize() {
        var runtime = new ModAttachedAgentRuntime(
            new ModAttachmentRecord(AgentOf(Session), Session, Key, null, _time.GetUtcNow()), () => "connection-1", _time);

        var chunks = 0;
        await foreach (var _ in runtime.ReadOutputAsync()) chunks++;

        await Assert.That(runtime.EmitsTerminalOutput).IsFalse();
        await Assert.That(chunks).IsEqualTo(0);
        await Assert.That(async () => await runtime.SendSpecialKeyAsync("Enter")).Throws<NotSupportedException>();
        await Assert.That(async () => await runtime.SendRawInputAsync("x"u8.ToArray())).Throws<NotSupportedException>();
        await Assert.That(async () => await runtime.SendUserInputAsync("typed")).Throws<InputNotAdmittedException>();

        runtime.Resize(80, 24);

        await Assert.That(runtime.RuntimeTransport).IsEqualTo("mod");
        await Assert.That(runtime.HasExited).IsFalse();
    }

    /// <summary>The server rejected whatever it pushed on a connection that has gone, so the runtime never offers it.</summary>
    [Test]
    public async Task Only_what_came_on_the_current_connection_is_offered() {
        var connection = "connection-1";
        var runtime    = new ModAttachedAgentRuntime(
            new ModAttachmentRecord(AgentOf(Session), Session, Key, null, _time.GetUtcNow()), () => connection, _time);
        var earlier = Guid.NewGuid();
        var current = Guid.NewGuid();

        await runtime.SendDispatchedInputAsync("before", earlier, waitForWrite: false);
        connection = "connection-2";
        await runtime.SendDispatchedInputAsync("after", current, waitForWrite: false);
        await runtime.SendDispatchedInputAsync("after", current, waitForWrite: false);

        await Assert.That(runtime.OldestHeld("connection-2")).IsEqualTo((current, "after"));
        await Assert.That(runtime.HeldCount).IsEqualTo(1);
    }

    [Test]
    [Arguments("4242:Sat Nov  8 10:00:00 2026", 4242)]
    [Arguments("4242", 0)]
    [Arguments(":4242", 0)]
    [Arguments("-1:x", 0)]
    [Arguments("pid:4242", 0)]
    public async Task The_pid_is_read_off_the_front_of_the_process_key(string processKey, int pid) =>
        await Assert.That(ModAttachedAgentRuntime.PidOf(processKey)).IsEqualTo(pid);
}
