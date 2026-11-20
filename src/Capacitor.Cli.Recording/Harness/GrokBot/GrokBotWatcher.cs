using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.GrokBot;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>
/// Records every Grok Bot chat as kcap sessions, read from the gateway on a Grok Bot cloud computer or from
/// the desktop app's own copy of each chat. The gateway lists each chat as an agent: a Bot's own thread, and
/// every new chat as a group, so groups are read like any other. One instance per computer, held by a lock
/// file; delivery state advances only after the server accepts a call, so a restart resends nothing and
/// loses nothing.
/// </summary>
public sealed class GrokBotWatcher(ConfigRoot config, ProfileContext profiles, UserHome home, ICapacitorHttpClient http, TimeProvider time) {
    static readonly TimeSpan BackoffCeiling = TimeSpan.FromMinutes(5);

    // The fingerprint only spares reads; a chat is re-read at least this often whatever it says, so a gateway
    // field that stops moving delays capture instead of stopping it.
    static readonly TimeSpan FullReadInterval = TimeSpan.FromMinutes(1);

    public string GatewayDescriptor => Path.Join(home.Path, "sand-data", "gateway.json");

    /// <summary>The server's scheme, host and port: a URL may carry more than that, and this goes in a log.</summary>
    string Destination => Uri.TryCreate(profiles.Resolution.ServerUrl, UriKind.Absolute, out var url) ? url.GetLeftPart(UriPartial.Authority) : "an unnamed server";

    string LockPath   => config.Path("grok-bot", "watch.lock");
    string StatusPath => config.Path("grok-bot", "watch.json");

    /// <summary>The watcher running on this computer, or null when none is. The lock is the proof that one
    /// runs; its status file is believed only while the process it names is alive, since the file outlives
    /// the watcher that wrote it and a log line cannot be tied to a running process at all.</summary>
    public GrokBotWatchStatus? Running() {
        if (!File.Exists(LockPath)) return null;

        try {
            using var free = new FileStream(LockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            return null;
        } catch (IOException) {
            // Held: a watcher is running.
        }

        try {
            var status = JsonNode.Parse(File.ReadAllTextShared(StatusPath));

            if (status?["pid"]?.GetValue<int>() is { } pid && status["server"]?.GetValue<string>() is { } server) {
                using var writer = Process.GetProcessById(pid);

                return new GrokBotWatchStatus(server, pid);
            }
        } catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or FormatException) {
            // Unreadable, or left by a process that is gone.
        }

        return new GrokBotWatchStatus(null, null);
    }

    /// <param name="log">Receives one line per thing worth telling whoever runs the watcher.</param>
    public async Task<GrokBotWatchOutcome> RunAsync(GrokBotWatchOptions options, Action<string> log, CancellationToken ct) {
        using var plain = new HttpClient();
        plain.Timeout = TimeSpan.FromSeconds(30);

        if (Source(options, plain) is not { } source) return GrokBotWatchOutcome.NoSource;

        // A dry run sends and saves nothing, so it may run beside the watcher that holds the lock.
        FileStream? held = null;
        if (!options.DryRun) {
            Directory.CreateDirectory(config.Path("grok-bot"));
            try {
                held = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            } catch (IOException) {
                return GrokBotWatchOutcome.AlreadyRunning;
            }

            // First thing under the lock, so a held lock never vouches for the watcher before this one.
            var status = StatusPath + ".tmp";
            await File.WriteAllTextAsync(status, new JsonObject { ["pid"] = Environment.ProcessId, ["server"] = Destination }.ToJsonString(), ct);
            File.Move(status, StatusPath, overwrite: true);
        }

        using var _ = held;

        var store   = GrokBotStateStore.For(config, profiles.Resolution.ServerUrl!, profiles.Name);
        var run     = new Run(source, store, store.Load(), options, log);
        var backoff = new GrokBotBackoff(options.Interval, BackoffCeiling);

        // Names the destination: a watcher keeps the server it started with, and whoever finds it running
        // has no other way to tell where it records.
        log($"watching {source.Description}, posting to {Destination}, gap {options.Gap.TotalMinutes:0} min, {run.States.Count} chat(s) in state");

        while (true) {
            string? failure;
            try {
                failure = await CycleAsync(run, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return GrokBotWatchOutcome.Stopped;
            } catch (Exception ex) {
                failure = $"cycle failed: {ex.Message}";
            }

            if (failure is null) {
                if (backoff.Succeeded() is > 0 and var ended) log($"recovered after {ended} failed cycle(s)");
            } else if (backoff.Failed(failure) is { } report) {
                log($"{report}; retrying with backoff, next in {backoff.NextDelay.TotalSeconds:0}s");
            }

            if (options.Once) return failure is null ? GrokBotWatchOutcome.Completed : GrokBotWatchOutcome.Failed;

            try {
                await Task.Delay(backoff.NextDelay, time, ct);
            } catch (OperationCanceledException) {
                return GrokBotWatchOutcome.Stopped;
            }
        }
    }

    /// <summary>What one run carries from cycle to cycle.</summary>
    sealed record Run(IGrokBotSource Source, GrokBotStateStore Store, Dictionary<string, GrokBotBotState> States, GrokBotWatchOptions Options, Action<string> Log) {
        public Dictionary<string, (string Fingerprint, DateTimeOffset ReadAt)> Seen { get; } = new(StringComparer.Ordinal);

        public HashSet<string> WithoutBeginning { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Unread { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The source the options name, else the gateway where this computer has one, else the desktop
    /// app's chats. A cloud computer has no desktop app, so the gateway's presence settles which it is.</summary>
    IGrokBotSource? Source(GrokBotWatchOptions options, HttpClient plain) {
        if (options.Gateway is { } descriptor) return File.Exists(descriptor) ? new GrokBotGateway(descriptor, plain) : null;
        if (options.Desktop is { } dir) return Directory.Exists(dir) ? new GrokBotDesktopChats(dir) : null;
        if (File.Exists(GatewayDescriptor)) return new GrokBotGateway(GatewayDescriptor, plain);

        return GrokBotDesktopPaths.HasChatStore(home) ? new GrokBotDesktopChats(GrokBotDesktopPaths.ChatStore(home)!) : null;
    }

    /// <summary>One pass over every chat. Returns why the cycle stopped short, or null when it did not; a
    /// refused call ends the cycle, since a lapsed sign-in or a server outage refuses every chat alike.</summary>
    async Task<string?> CycleAsync(Run run, CancellationToken ct) {
        var client = await http.ForBackgroundAsync(ct);

        foreach (var agent in await run.Source.ListAgentsAsync(ct)) {
            var state   = run.States.GetValueOrDefault(agent.Id, GrokBotBotState.Empty);
            var now     = time.GetUtcNow();
            var changed = !run.Seen.TryGetValue(agent.Id, out var last)
                       || last.Fingerprint != agent.Fingerprint
                       || now - last.ReadAt >= FullReadInterval;

            if (!changed && state.Open is null) continue;

            var read    = GrokBotSessionizer.NeedsRead(state, changed, now.ToUnixTimeMilliseconds(), run.Options.Gap) ? await run.Source.ReadSinceAsync(agent.Id, state.LastSeq, ct) : GrokBotRead.Nothing;

            if (read.Failed) {
                // Said once per stretch; the chat is read again next cycle.
                if (run.Unread.Add(agent.Id)) run.Log($"chat {agent.Id}: could not be read; nothing is sent for it until it can");
                continue;
            }

            run.Unread.Remove(agent.Id);

            var actions = GrokBotSessionizer.Plan(agent.Id, state, read.Entries, now.ToUnixTimeMilliseconds(), agent.IsRunningTurn, run.Options.Gap, read.FromThreadStart);

            if (run.Options.DryRun) {
                var start = read.FromThreadStart ? "" : ", without the chat's beginning";
                run.Log($"{(agent.Name.Length > 0 ? $"{agent.Name} ({agent.Id})" : agent.Id)}: {read.Entries.Count} entries read{start}, {actions.Count} call(s) planned");
                foreach (var action in actions) run.Log("  would " + Describe(action));
                continue;
            }

            if (!read.FromThreadStart && state == GrokBotBotState.Empty && actions.Count == 0 && run.WithoutBeginning.Add(agent.Id))
                run.Log($"chat {agent.Id}: its beginning is not available here, so recording starts at its next quiet gap");

            foreach (var action in actions) {
                if (await SendAsync(client, action, ct) is { } refused) {
                    run.Seen.Remove(agent.Id);
                    return refused;
                }

                run.Log(Describe(action));
                run.States[agent.Id] = action.After;
                run.Store.Save(run.States);
            }

            if (changed) run.Seen[agent.Id] = (agent.Fingerprint, now);
        }

        return null;
    }

    /// <summary>Sends one call; returns why the server refused it, or null when it was accepted.</summary>
    async Task<string?> SendAsync(HttpClient client, GrokBotAction action, CancellationToken ct) {
        var baseUrl = profiles.Resolution.ServerUrl!.TrimEnd('/');

        var (route, session, body) = action switch {
            GrokBotStartSession start => ("hooks/session-start/grok-bot", start.SessionId, StartBody(start)),
            GrokBotSendLines lines    => ("hooks/transcript", lines.SessionId, TranscriptBody(lines)),
            GrokBotEndSession end     => ("hooks/session-end/grok-bot", end.SessionId, new JsonObject {
                ["session_id"] = end.SessionId,
                ["ended_at"]   = Iso(end.EndedAtMs),
                ["reason"]     = "idle"
            }.ToJsonString()),
            _ => throw new InvalidOperationException(action.GetType().Name)
        };

        using var content  = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostWithRetryAsync($"{baseUrl}/{route}", content, time, ct: ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return "the server refused the sign-in (401); run `kcap login --device` on this computer";

        return response.IsSuccessStatusCode ? null : $"{route} answered {(int)response.StatusCode} for session {session}";
    }

    static string Describe(GrokBotAction action) => action switch {
        GrokBotStartSession a => $"open session {a.SessionId} at {Iso(a.StartedAtMs)}",
        GrokBotSendLines a    => $"send {a.Lines.Length} entr{(a.Lines.Length == 1 ? "y" : "ies")} (seq {a.Seqs[0]}..{a.Seqs[^1]}) to {a.SessionId}",
        GrokBotEndSession a   => $"end session {a.SessionId} at {Iso(a.EndedAtMs)}",
        _                     => action.GetType().Name
    };

    // Strict, so a server that does not know the vendor refuses the batch instead of advancing past
    // lines it stored nothing for.
    internal static string TranscriptBody(GrokBotSendLines lines) => new JsonObject {
        ["session_id"]   = lines.SessionId,
        ["lines"]        = new JsonArray([.. lines.Lines.Select(line => (JsonNode?)JsonValue.Create(line))]),
        ["line_numbers"] = new JsonArray([.. lines.Seqs.Select(seq => (JsonNode?)JsonValue.Create(seq))]),
        ["vendor"]       = "grok-bot",
        ["strict"]       = true
    }.ToJsonString();

    // Always private, whatever the profile's default: a Bot reads mail, chat and documents.
    string StartBody(GrokBotStartSession start) => new JsonObject {
        ["session_id"]         = start.SessionId,
        ["started_at"]         = Iso(start.StartedAtMs),
        ["default_visibility"] = "private",
        ["home_dir"]           = home.Path
    }.ToJsonString();

    static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O");
}
