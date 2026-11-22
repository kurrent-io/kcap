using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Setup.Intelligence;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One answer the Background intelligence tab offers to who runs it: a local agent, a provider, or none.
public sealed class BackgroundIntelligenceOption : ReactiveObject {
    public const string ProviderToken = IntelligenceOption.ProviderToken;

    bool _found = true;
    bool _isSelected;

    BackgroundIntelligenceOption(string token, string label, string? detail, bool isAgent) {
        Token   = token;
        Label   = label;
        Detail  = detail;
        IsAgent = isAgent;
    }

    internal static BackgroundIntelligenceOption LocalAgent(string harness) => new(harness, IntelligenceOption.AgentLabel(harness), null, true);

    internal static BackgroundIntelligenceOption Provider() =>
        new(ProviderToken, "A provider with your API key", "Anthropic, OpenAI, OpenRouter or Cohere", false);

    internal static BackgroundIntelligenceOption None() =>
        new(IntelligenceAnswer.NoneToken, "None", "Sessions keep their opening message as a title", false);

    public string  Token   { get; }
    public string  Label   { get; }
    public string? Detail  { get; }
    public bool    IsAgent { get; }

    /// Whether this computer can run the agent; only an agent can be missing.
    public bool Found {
        get => _found;
        internal set {
            this.RaiseAndSetIfChanged(ref _found, value);
            this.RaisePropertyChanged(nameof(CanSelect));
            this.RaisePropertyChanged(nameof(NotFound));
        }
    }

    public bool CanSelect => Found;

    public bool NotFound => !Found;

    public bool IsSelected {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}
