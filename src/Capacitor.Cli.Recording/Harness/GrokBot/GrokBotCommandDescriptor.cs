using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>The descriptor read from the standard output of a command the operator configured. On a Grok Bot
/// cloud computer the descriptor is owned by another user and unreadable to a Bot's own user, so a command
/// (for example <c>sudo -n cat …/gateway.json</c>) is what reaches it. The command is run directly, never
/// through a shell, so it carries no quoting or pipes, and it must be non-interactive and exit promptly. Its
/// output holds the bearer token, so the token never enters a message or log. A short cache spares one run
/// per gateway call without outlasting a token rotation.</summary>
public sealed class GrokBotCommandDescriptor(string command, IProcessRunner runner, TimeProvider time) : GrokBotDescriptor {
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    (DateTimeOffset At, string Json)? _cached;

    public override string Describe => "the gateway via the configured command";

    public override async Task<string> ReadJsonAsync(CancellationToken ct) {
        if (_cached is { } last && time.GetUtcNow() - last.At < CacheFor) return last.Json;

        var json = await RunAsync(ct);
        _cached  = (time.GetUtcNow(), json);

        return json;
    }

    async Task<string> RunAsync(CancellationToken ct) {
        var argv = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (argv.Length == 0) throw new InvalidOperationException("the Grok Bot gateway command is empty");

        var result = await runner.RunAsync(argv[0], argv[1..], new RunOptions(Timeout: Deadline), ct);

        // A command that outruns the deadline is a misconfiguration to surface, not a hang: it must be
        // non-interactive and exit promptly.
        if (result.TimedOut)
            throw new InvalidOperationException($"the Grok Bot gateway command did not finish within {Deadline.TotalSeconds:0}s");

        // The standard output carries the token, so only the exit code and standard error — the command's own
        // diagnostics, not the descriptor — name the failure.
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"the Grok Bot gateway command exited {result.ExitCode}: {result.Stderr.Trim()}");

        return result.Stdout;
    }
}
