using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.GrokBot;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// <c>kcap grok-bot watch</c>: records every Grok Bot chat as kcap sessions, read from the gateway on a
/// Grok Bot cloud computer or from the desktop app's own copy of each chat. The gateway lists each chat as
/// an agent: a Bot's own thread, and every new chat as a group, so groups are read like any other. One
/// instance per computer, held by a lock file; delivery state advances only after the server accepts a
/// call, so a restart resends nothing and loses nothing.
/// </summary>
public sealed class GrokBotWatchCommand(ConfigRoot config, ProfileContext profiles, UserHome home, ICapacitorHttpClient http, TimeProvider time) {
    static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan DefaultGap      = TimeSpan.FromHours(2);
    static readonly TimeSpan BackoffCeiling  = TimeSpan.FromMinutes(5);

    // The fingerprint only spares reads; a chat is re-read at least this often whatever it says, so a gateway
    // field that stops moving delays capture instead of stopping it.
    static readonly TimeSpan FullReadInterval = TimeSpan.FromMinutes(1);

    readonly HashSet<string> _withoutBeginning = new(StringComparer.Ordinal);

    public async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2 || args[1] != "watch") {
            await Console.Error.WriteLineAsync("Usage: kcap grok-bot watch [--once] [--dry-run] [--gateway <gateway.json> | --desktop <dir>] [--interval <seconds>] [--gap-minutes <n>]");
            return 1;
        }

        var interval   = int.TryParse(Option(args, "--interval"), out var s) && s > 0 ? TimeSpan.FromSeconds(s) : DefaultInterval;
        var gap        = int.TryParse(Option(args, "--gap-minutes"), out var g) && g > 0 ? TimeSpan.FromMinutes(g) : DefaultGap;
        var once       = args.Contains("--once");
        var dryRun     = args.Contains("--dry-run");

        using var plain = new HttpClient();
        plain.Timeout = TimeSpan.FromSeconds(30);

        if (Source(args, plain) is not { } source) {
            await Console.Error.WriteLineAsync(
                $"No Grok Bot here: no gateway at {GatewayDescriptor}, and no desktop app chats at {GrokBotDesktopPaths.ChatStore(home) ?? "(no application data directory)"}. "
              + "Run this on a Grok Bot cloud computer or on a computer with the Grok Bot desktop app, or pass --gateway or --desktop.");
            return 1;
        }

        Directory.CreateDirectory(config.Path("grok-bot"));
        FileStream lockFile;
        try {
            lockFile = new FileStream(config.Path("grok-bot", "watch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        } catch (IOException) {
            await Console.Error.WriteLineAsync("kcap grok-bot watch is already running on this computer.");
            return 1;
        }

        using var _   = lockFile;
        using var cts = new CancellationTokenSource();

        void Cancel(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; cts.Cancel(); }

        Console.CancelKeyPress += Cancel;

        try {
            return await WatchAsync(source, interval, gap, once, dryRun, cts.Token);
        } finally {
            Console.CancelKeyPress -= Cancel;
        }
    }

    async Task<int> WatchAsync(IGrokBotSource source, TimeSpan interval, TimeSpan gap, bool once, bool dryRun, CancellationToken ct) {
        var store   = GrokBotStateStore.For(config, profiles.Resolution.ServerUrl!, profiles.Name);
        var states  = store.Load();
        var seen    = new Dictionary<string, (string Fingerprint, DateTimeOffset ReadAt)>(StringComparer.Ordinal);
        var backoff = new GrokBotBackoff(interval, BackoffCeiling);

        Log($"watching {source.Description}, gap {gap.TotalMinutes:0} min, {states.Count} chat(s) in state");

        do {
            string? failure;
            try {
                failure = await CycleAsync(source, store, states, seen, gap, dryRun, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                failure = $"cycle failed: {ex.Message}";
            }

            if (failure is null) {
                if (backoff.Succeeded() is > 0 and var ended) Log($"recovered after {ended} failed cycle(s)");
            } else if (backoff.Failed(failure) is { } report) {
                Log($"{report}; retrying with backoff, next in {backoff.NextDelay.TotalSeconds:0}s");
            }

            if (once) return failure is null ? 0 : 1;

            try {
                await Task.Delay(backoff.NextDelay, time, ct);
            } catch (OperationCanceledException) {
                break;
            }
        } while (true);

        return 0;
    }

    string GatewayDescriptor => Path.Join(home.Path, "sand-data", "gateway.json");

    /// <summary>The source the flags name, else the gateway where this computer has one, else the desktop
    /// app's chats. A cloud computer has no desktop app, so the gateway's presence settles which it is.</summary>
    IGrokBotSource? Source(string[] args, HttpClient plain) {
        if (Option(args, "--gateway") is { } descriptor) return File.Exists(descriptor) ? new GrokBotGateway(descriptor, plain) : null;
        if (Option(args, "--desktop") is { } dir) return Directory.Exists(dir) ? new GrokBotDesktopChats(dir) : null;
        if (File.Exists(GatewayDescriptor)) return new GrokBotGateway(GatewayDescriptor, plain);

        return GrokBotDesktopPaths.ChatStore(home) is { } store && Directory.Exists(store) ? new GrokBotDesktopChats(store) : null;
    }

    /// <summary>One pass over every chat. Returns why the cycle stopped short, or null when it did not; a
    /// refused call ends the cycle, since a lapsed sign-in or a server outage refuses every chat alike.</summary>
    async Task<string?> CycleAsync(
            IGrokBotSource                       source,
            GrokBotStateStore                    store,
            Dictionary<string, GrokBotBotState>  states,
            Dictionary<string, (string Fingerprint, DateTimeOffset ReadAt)> seen,
            TimeSpan                             gap,
            bool                                 dryRun,
            CancellationToken                    ct
        ) {
        var client = await http.ForBackgroundAsync(ct);

        foreach (var agent in await source.ListAgentsAsync(ct)) {
            var state   = states.GetValueOrDefault(agent.Id, GrokBotBotState.Empty);
            var now     = time.GetUtcNow();
            var changed = !seen.TryGetValue(agent.Id, out var last)
                       || last.Fingerprint != agent.Fingerprint
                       || now - last.ReadAt >= FullReadInterval;

            if (!changed && state.Open is null) continue;

            var read    = GrokBotSessionizer.NeedsRead(state, changed, now.ToUnixTimeMilliseconds(), gap) ? await source.ReadSinceAsync(agent.Id, state.LastSeq, ct) : GrokBotRead.Nothing;
            var actions = GrokBotSessionizer.Plan(agent.Id, state, read.Entries, now.ToUnixTimeMilliseconds(), agent.IsRunningTurn, gap, read.FromThreadStart);

            if (dryRun) {
                var start = read.FromThreadStart ? "" : ", without the chat's beginning";
                Log($"{(agent.Name.Length > 0 ? $"{agent.Name} ({agent.Id})" : agent.Id)}: {read.Entries.Count} entries read{start}, {actions.Count} call(s) planned");
                foreach (var action in actions) Log("  would " + Describe(action));
                continue;
            }

            if (!read.FromThreadStart && state == GrokBotBotState.Empty && actions.Count == 0 && _withoutBeginning.Add(agent.Id))
                Log($"chat {agent.Id}: its beginning is not available here, so recording starts at its next quiet gap");

            foreach (var action in actions) {
                if (await SendAsync(client, action, ct) is { } refused) {
                    seen.Remove(agent.Id);
                    return refused;
                }

                states[agent.Id] = action.After;
                store.Save(states);
            }

            if (changed) seen[agent.Id] = (agent.Fingerprint, now);
        }

        return null;
    }

    /// <summary>Sends one call; returns why the server refused it, or null when it was accepted.</summary>
    async Task<string?> SendAsync(HttpClient client, GrokBotAction action, CancellationToken ct) {
        var baseUrl = profiles.Resolution.ServerUrl!;

        var (route, body) = action switch {
            GrokBotStartSession start => ("hooks/session-start/grok-bot", StartBody(start)),
            GrokBotSendLines lines    => ("hooks/transcript", JsonSerializer.Serialize(new TranscriptBatch {
                SessionId   = lines.SessionId,
                Lines       = lines.Lines,
                LineNumbers = lines.Seqs,
                Vendor      = "grok-bot",
                Strict      = true
            }, CapacitorJsonContext.Default.TranscriptBatch)),
            GrokBotEndSession end     => ("hooks/session-end/grok-bot", new JsonObject {
                ["session_id"] = end.SessionId,
                ["ended_at"]   = Iso(end.EndedAtMs),
                ["reason"]     = "idle"
            }.ToJsonString()),
            _ => throw new InvalidOperationException(action.GetType().Name)
        };

        using var content  = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostWithRetryAsync($"{baseUrl}/{route}", content, time, ct: ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return "the server refused the sign-in (401); run `kcap login --device` on this computer";

        if (!response.IsSuccessStatusCode)
            return $"{route} answered {(int)response.StatusCode} for session {SessionOf(action)}";

        Log(Describe(action));
        return null;
    }

    static string Describe(GrokBotAction action) => action switch {
        GrokBotStartSession a => $"open session {a.SessionId} at {Iso(a.StartedAtMs)}",
        GrokBotSendLines a    => $"send {a.Lines.Length} entr{(a.Lines.Length == 1 ? "y" : "ies")} (seq {a.Seqs[0]}..{a.Seqs[^1]}) to {a.SessionId}",
        GrokBotEndSession a   => $"end session {a.SessionId} at {Iso(a.EndedAtMs)}",
        _                     => action.GetType().Name
    };

    // Always private, whatever the profile's default: a Bot reads mail, chat and documents.
    string StartBody(GrokBotStartSession start) => new JsonObject {
        ["session_id"]         = start.SessionId,
        ["started_at"]         = Iso(start.StartedAtMs),
        ["default_visibility"] = "private",
        ["home_dir"]           = home.Path
    }.ToJsonString();

    static string SessionOf(GrokBotAction action) => action switch {
        GrokBotStartSession a => a.SessionId,
        GrokBotSendLines a    => a.SessionId,
        GrokBotEndSession a   => a.SessionId,
        _                     => "?"
    };

    static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O");

    static string? Option(string[] args, string name) {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    void Log(string message) => Console.Error.WriteLine($"{time.GetUtcNow():O} {message}");
}
