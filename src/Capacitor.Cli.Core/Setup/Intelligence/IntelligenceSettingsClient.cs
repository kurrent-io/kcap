using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Capacitor.Cli.Core.Setup.Intelligence;

/// <summary>Reads the local server's background intelligence settings and writes its preferences. Never throws short
/// of cancellation.</summary>
public static class IntelligenceSettingsClient {
    /// <summary>The code a server without these routes is refused with, so a caller can tell it from a server's own
    /// refusal.</summary>
    public const string NeedsUpdate = "needs_update";

    public static string PreferencesUrl(string serverUrl) => $"{IntelligenceSetupClient.Url(serverUrl)}/preferences";

    public static Task<IntelligenceSettingsOutcome> ReadAsync(HttpClient http, string serverUrl, CancellationToken ct) =>
        SendAsync(() => http.GetAsync(IntelligenceSetupClient.Url(serverUrl), ct), ct);

    public static Task<IntelligenceSettingsOutcome> UpdateAsync(
            HttpClient http, string serverUrl, IntelligencePreferencesRequest request, CancellationToken ct) =>
        SendAsync(() => http.PutAsJsonAsync(PreferencesUrl(serverUrl), request, CapacitorJsonContext.Default.IntelligencePreferencesRequest, ct), ct);

    static async Task<IntelligenceSettingsOutcome> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct) {
        try {
            using var response = await send().ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound) return new IntelligenceSettingsOutcome.Refused(NeedsUpdate);

            if (response.StatusCode == HttpStatusCode.BadRequest && Parse(body, CapacitorJsonContext.Default.IntelligenceSetupError) is { Error.Length: > 0 } refusal)
                return new IntelligenceSettingsOutcome.Refused(refusal.Error);

            if (!response.IsSuccessStatusCode) return new IntelligenceSettingsOutcome.Failed($"HTTP {(int)response.StatusCode}");

            return Parse(body, CapacitorJsonContext.Default.IntelligenceSettings) is { Mode: not null, Resolving: not null } settings
                ? new IntelligenceSettingsOutcome.Read(settings)
                : new IntelligenceSettingsOutcome.Failed("the server's answer was unreadable");
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            return new IntelligenceSettingsOutcome.Failed(ex.Message);
        }
    }

    static T? Parse<T>(string body, JsonTypeInfo<T> type) where T : class {
        try {
            return JsonSerializer.Deserialize(body, type);
        } catch (JsonException) {
            return null;
        }
    }
}
