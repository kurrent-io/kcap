using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Headless;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Headless;

public class HeadlessFailureClassifierTests {
    const string PiCredit = """400 {"type":"error","error":{"type":"invalid_request_error","message":"Your credit balance is too low to access the Anthropic API. Please go to Plans & Billing to upgrade or purchase credits."},"request_id":"req_x"}""";
    const string PiBadKey = """401 {"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"},"request_id":"req_x"}""";
    const string OpenCodeLimit = "Free usage limit reached. Free models work in OpenCode (https://opencode.ai); subscribe to OpenCode Go";

    [Test]
    [Arguments(401, null, nameof(ClaudeCliFailure.NotSignedIn))]
    [Arguments(403, null, nameof(ClaudeCliFailure.NotSignedIn))]
    [Arguments(401, "quota exceeded", nameof(ClaudeCliFailure.NotSignedIn))]
    [Arguments(429, null, nameof(ClaudeCliFailure.SpendBudget))]
    [Arguments(400, PiCredit, nameof(ClaudeCliFailure.SpendBudget))]
    [Arguments(null, OpenCodeLimit, nameof(ClaudeCliFailure.SpendBudget))]
    [Arguments(null, "Rate Limit hit", nameof(ClaudeCliFailure.SpendBudget))]
    [Arguments(null, "No models available", nameof(ClaudeCliFailure.NotSignedIn))]
    [Arguments(null, "No API key for provider", nameof(ClaudeCliFailure.NotSignedIn))]
    [Arguments(null, "OAuth refresh failed for anthropic: token refresh request failed", nameof(ClaudeCliFailure.NotSignedIn))]
    [Arguments(400, "{\"error\": \"invalid_grant\"}", nameof(ClaudeCliFailure.NotSignedIn))]
    [Arguments(500, "overloaded", nameof(ClaudeCliFailure.ProcessFailure))]
    [Arguments(null, null, nameof(ClaudeCliFailure.ProcessFailure))]
    public async Task A_failure_is_classified_from_status_and_message(int? status, string? message, string expected) =>
        await Assert.That(HeadlessFailureClassifier.Classify(status, message).ToString()).IsEqualTo(expected);

    [Test]
    [Arguments(PiBadKey, 401)]
    [Arguments(PiCredit, 400)]
    [Arguments("429", 429)]
    [Arguments("429 slow down", 429)]
    [Arguments("401: {\"message\":\"Invalid or missing API key.\"}", 401)]
    [Arguments("  503 x", null)]
    [Arguments("4011 x", null)]
    [Arguments("99 x", null)]
    [Arguments("600 x", null)]
    [Arguments("401{", null)]
    [Arguments("", null)]
    [Arguments(null, null)]
    public async Task A_leading_status_is_read_from_the_message(string? message, int? expected) =>
        await Assert.That(HeadlessFailureClassifier.StatusFromMessage(message)).IsEqualTo(expected);
}
