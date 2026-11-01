using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli;

/// <summary>
/// Opt-in phase timing for <c>kcap hook</c>: with <c>KCAP_HOOK_TIMING=1</c> every phase appends a line
/// to <c>logs/hook-timing.log</c> in the config directory, never to stdout, which the agent reads as
/// the hook's answer. Ambient because the phases span the entry point and the vendor handlers; when
/// off, each call is one null check.
/// </summary>
public static class HookTiming {
    public const string EnvVar = "KCAP_HOOK_TIMING";

    static HookTimingLog? _log;

    public static bool IsRequested(Func<string, string?> env) => env(EnvVar) is "1" or "true";

    /// <summary>Starts timing this process when it is a hook and the variable asks for it.</summary>
    public static void Begin(string[] args, Func<string, string?> env) {
        if (_log is not null || args is not ["hook", ..] || !IsRequested(env)) return;

        var log = _log = new HookTimingLog(TimeProvider.System, ProcessStart(), Environment.ProcessId, string.Join(' ', args[1..]));
        log.Mark("main");

        AppDomain.CurrentDomain.ProcessExit += (_, _) => log.Mark("exit");
    }

    public static void Open(string logsDirectory) => _log?.Open(Path.Combine(logsDirectory, HookTimingLog.FileName));

    public static void Describe(string? eventName, string? sessionId) => _log?.Describe(eventName, sessionId);

    /// <summary><see cref="Describe"/> from a hook payload's <c>hook_event_name</c> and <c>session_id</c>;
    /// parsed only while timing is on.</summary>
    public static void DescribeFrom(string body) {
        if (_log is not { } log) return;

        try {
            if (JsonNode.Parse(body) is not JsonObject hook) return;

            log.Describe(Text(hook, "hook_event_name"), Text(hook, "session_id"));
        } catch (JsonException) { }

        static string? Text(JsonObject hook, string key) =>
            hook[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    public static void Mark(string phase) => _log?.Mark(phase);

    static DateTimeOffset? ProcessStart() {
        try {
            using var self = Process.GetCurrentProcess();

            return new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero);
        } catch {
            return null;
        }
    }
}
