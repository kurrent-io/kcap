using System.Diagnostics;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>Where the gateway descriptor JSON — scheme, host, port, token — is read from. The gateway reads
/// it live on every call, because the gateway rewrites it with a new port and token on each restart.</summary>
public abstract class GrokBotDescriptor {
    public abstract string Describe { get; }

    /// <summary>The descriptor JSON, read now. Throws when it cannot be obtained.</summary>
    public abstract string ReadJson();
}

/// <summary>The descriptor read straight from a file the watcher's own user can open — the desktop app, or a
/// cloud computer that still leaves the descriptor readable.</summary>
public sealed class GrokBotFileDescriptor(string path) : GrokBotDescriptor {
    public override string Describe => $"the gateway at {path}";

    // Shared read: the gateway rewrites the descriptor on each restart, and the read must not deny that write.
    public override string ReadJson() => File.ReadAllTextShared(path);
}

/// <summary>The descriptor read from the standard output of a command the operator configured. On a Grok Bot
/// cloud computer the descriptor is owned by another user and unreadable to a Bot's own user, so a command
/// (for example <c>sudo -n cat …/gateway.json</c>) is what reaches it. The command is run directly, never
/// through a shell, so it carries no quoting or pipes, and it must be non-interactive and exit promptly. Its
/// output holds the bearer token, so the token never enters a message or log. A short cache spares one run
/// per gateway call without outlasting a token rotation.</summary>
public sealed class GrokBotCommandDescriptor(string command, TimeProvider time) : GrokBotDescriptor {
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    (DateTimeOffset At, string Json)? _cached;

    public override string Describe => "the gateway via the configured command";

    public override string ReadJson() {
        if (_cached is { } last && time.GetUtcNow() - last.At < CacheFor) return last.Json;

        var json = Run();
        _cached  = (time.GetUtcNow(), json);

        return json;
    }

    string Run() {
        var argv = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (argv.Length == 0) throw new InvalidOperationException("the Grok Bot gateway command is empty");

        using var process = new Process {
            StartInfo = new ProcessStartInfo {
                FileName               = argv[0],
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            }
        };
        foreach (var arg in argv[1..]) process.StartInfo.ArgumentList.Add(arg);

        process.Start();

        // Drain both pipes before waiting: a child that fills one would otherwise block, and the wait with it.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        // Left to finish on its own on a timeout: the configured command must exit promptly, so a stall is a
        // misconfiguration, not a hang to chase with a kill that could land on the wrong pid.
        if (!process.WaitForExit((int)Deadline.TotalMilliseconds))
            throw new InvalidOperationException($"the Grok Bot gateway command did not finish within {Deadline.TotalSeconds:0}s");

        process.WaitForExit();

        // The standard output carries the token, so only the exit code and standard error — the command's own
        // diagnostics, not the descriptor — name the failure.
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"the Grok Bot gateway command exited {process.ExitCode}: {stderr.Result.Trim()}");

        return stdout.Result;
    }
}
