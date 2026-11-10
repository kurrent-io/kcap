using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Generates a session's title after its watcher has gone. A stop request leaves the watcher a few seconds
/// before the kill, and the title CLI takes longer than that, so the watcher hands the title to this detached
/// process instead of dropping it. It reads the transcript itself: the prompt never goes on a command line.
/// </summary>
sealed class GenerateTitleCommand(ConfigRoot config, ProfileContext profiles, HarnessRegistry harnesses, ICapacitorHttpClient http, TimeProvider time) {
    public const string Name = TitleGeneration.HandOffCommand;

    public async Task<int> HandleAsync(string baseUrl, string sessionId, string vendor, string transcriptPath) {
        var logDir = config.Path("logs");
        Directory.CreateDirectory(logDir);
        var logWriter = new StreamWriter(Path.Combine(logDir, $"{sessionId}-title.log"), append: true) { AutoFlush = true };
        Console.SetOut(logWriter);
        Console.SetError(logWriter);

        try {
            return await GenerateAsync(baseUrl, sessionId, vendor, transcriptPath);
        } finally {
            await logWriter.DisposeAsync();
        }
    }

    internal async Task<int> GenerateAsync(string baseUrl, string sessionId, string vendor, string transcriptPath) {
        if (IsDisabled(sessionId)) return 0;

        Log($"Generating the title of session {sessionId} ({vendor})");

        string? userText, assistantText;
        try {
            (userText, assistantText) = WatchCommand.ReadTitleContext(transcriptPath, vendor, time);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Log($"Could not read the transcript: {ex.Message}");
            return 1;
        }

        if (userText is null) {
            Log("The transcript holds no prompt to title the session from");
            return 0;
        }

        var result = await TitleGeneration.GenerateAsync(userText, assistantText, time, Log, profiles.Resolution.Profile, harnesses, config, vendor);

        if (result is null) {
            Log("Title generation returned no usable title");
            return 1;
        }

        // A title takes longer than a person takes to disable the session, and this process runs on by itself.
        if (IsDisabled(sessionId)) return 0;

        var payload = new SessionTitlePayload {
            SessionId        = sessionId,
            Title            = result.Result,
            Model            = result.Model,
            InputTokens      = result.InputTokens,
            OutputTokens     = result.OutputTokens,
            CacheReadTokens  = result.CacheReadTokens,
            CacheWriteTokens = result.CacheWriteTokens,
            Source           = "client-auto"
        };

        try {
            using var httpClient = await http.ForBackgroundAsync();
            using var content    = new StringContent(JsonSerializer.Serialize(payload, CapacitorJsonContext.Default.SessionTitlePayload), Encoding.UTF8, "application/json");
            using var response   = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/session-title", content, time);

            Log(response.IsSuccessStatusCode ? $"LLM title generated: {result.Result}" : $"Title POST returned HTTP {(int)response.StatusCode}");

            return response.IsSuccessStatusCode ? 0 : 1;
        } catch (HttpRequestException ex) {
            Log($"Server unreachable for the title POST: {ex.Message}");
            return 1;
        }
    }

    bool IsDisabled(string sessionId) {
        if (!DisabledSessions.IsDisabledByPerson(sessionId, config)) return false;

        Log($"Session {sessionId} was disabled, so it gets no title");

        return true;
    }

    void Log(string message) => Console.Error.WriteLine($"[{time.GetLocalNow():HH:mm:ss.fff}] [title] {message}");
}
