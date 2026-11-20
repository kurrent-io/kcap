using Capacitor.Cli.Core.Setup.Intelligence;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// One answer the background intelligence page offers: a local agent's harness, a provider, or not now.
public sealed class IntelligenceOption : ReactiveObject {
    public const string ProviderToken = "provider";

    IntelligenceOption(string token, string label, string detail) {
        Token  = token;
        Label  = label;
        Detail = detail;
    }

    internal static IntelligenceOption LocalAgent(string harness) {
        var label  = HeadlessHarnessProbe.Label(harness);
        var access = harness is HeadlessHarnessProbe.Pi or HeadlessHarnessProbe.OpenCode ? "sign-in or key" : "sign-in and quota";

        return new(harness, AgentLabel(harness), $"Runs on this machine through {label}'s own {access}. Nothing goes anywhere it does not already send.");
    }

    /// The local agent's name wherever the app offers it to run background intelligence.
    internal static string AgentLabel(string harness) => $"My local agent: {HeadlessHarnessProbe.Label(harness)}";

    internal static IntelligenceOption Provider() => new(ProviderToken, "A provider, with my API key",
        "Anthropic, OpenAI, OpenRouter or Cohere. kcap tries the key with one call before saving it.");

    internal static IntelligenceOption NotNow() => new(IntelligenceAnswer.NoneToken, "Not now",
        "Sessions keep their opening message as a title. Choose later with kcap local intelligence.");

    public string Token  { get; }
    public string Label  { get; }
    public string Detail { get; }

    public bool IsLocalAgent => Token is not ProviderToken and not IntelligenceAnswer.NoneToken;

    public bool IsSelected {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}
