using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Recording;

/// <summary>Posts the end a recorder synthesises for its session, the way a hook would: behind any spooled backlog for
/// the session, and into the spool on any status <see cref="HookSpool.IsRetryable"/> retries, so an outage or a lapsed
/// credential never drops it.</summary>
/// <param name="whatsDone">Starts the what's-done summary the server asks for, given the session and the vendor.</param>
public sealed class SessionEndPoster(
        ConfigRoot config, string? url, ICapacitorHttpClient http, TimeProvider time, Action<string> log, Action<string, string> whatsDone) {
    /// <summary>
    /// Total time budget for the post, client creation included. A server that has not yet seen the recorder's
    /// connection close waits up to 10s for a drain it already had before answering, so the budget outlasts that
    /// wait; an end that still misses it is spooled.
    /// </summary>
    public static readonly TimeSpan PostBudget = TimeSpan.FromSeconds(15);

    /// <summary>True when the server accepted the end or it is spooled for the drain. A session with a spooled backlog
    /// gets its end spooled behind it, so a stranded start still reaches the server first.</summary>
    public async Task<bool> PostAsync(string sessionId, string vendor, JsonObject endHook, RepositoryPayload? repository, string what) {
        if (repository is not null) {
            endHook["repository"] = JsonNode.Parse(
                JsonSerializer.Serialize(repository, RepositoryCacheJsonContext.Default.RepositoryPayload)
            );
        }

        var body  = endHook.ToJsonString();
        var spool = new HookSpool(config, time, url);

        bool Spool(string why) {
            var spooled = spool.Append(sessionId, $"session-end/{vendor}", body);
            log($"{what} session-end {why}; {(spooled ? "spooled for the next drain" : "the spool write failed, so it is lost")}");

            return spooled;
        }

        if (spool.HasBacklog(sessionId)) return Spool("queued behind the session's spooled hooks");

        using var budgetCts = new CancellationTokenSource(PostBudget, time);

        try {
            using var httpClient = await http.ForBackgroundAsync(budgetCts.Token);
            using var content    = new StringContent(body, Encoding.UTF8, "application/json");

            var target = $"{url}/hooks/session-end/{vendor}";
            using var response = await httpClient.PostWithRetryAsync(target, content, time, timeout: PostBudget, ct: budgetCts.Token);

            if (!response.IsSuccessStatusCode) {
                var code = (int)response.StatusCode;

                if (HookSpool.IsRetryable(code)) return Spool($"POST returned HTTP {code}");

                log($"{what} session-end POST returned HTTP {code}");
                return false;
            }

            log($"{what} session-end POST succeeded");

            try {
                var answer = await response.Content.ReadAsStringAsync(budgetCts.Token);
                var node   = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);

                if (node?["generate_whats_done"]?.GetValue<bool>() == true) whatsDone(sessionId, vendor);
            } catch (Exception ex) {
                log($"{what} session-end response parse failed: {ex.Message}");
            }

            return true;
        } catch (OperationCanceledException) {
            return Spool($"POST timed out after {PostBudget.TotalSeconds:F0}s");
        } catch (HttpRequestException ex) {
            return Spool($"POST failed: {ex.Message}");
        } catch (Exception ex) {
            log($"{what} session-end POST failed: {ex.Message}");
        }

        return false;
    }
}
