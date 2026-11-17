using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Driver;

/// <summary>Driver's chatbox. The first message starts a hosted agent primed with what Driver shows; from then on the
/// conversation is that agent's own chat, rendered by the session chat view, so questions, permissions and follow-ups
/// work as in any session. The agent is an ordinary session: it acts as the person, through tools that check access.</summary>
public sealed class DriverChatViewModel : ReactiveObject, IDisposable {
    readonly ILaunchClient                     _launch;
    readonly Func<string?>                     _daemonName;
    readonly Func<string?>                     _workingFolder;
    readonly Func<string>                      _context;
    readonly Func<string, WorkspaceViewModel>? _workspaces;
    readonly CancellationTokenSource           _shutdown = new();

    public DriverChatViewModel(ILaunchClient launch, Func<string?> daemonName, Func<string?> workingFolder, Func<string> context,
            Func<string, WorkspaceViewModel>? workspaces) {
        _launch        = launch;
        _daemonName    = daemonName;
        _workingFolder = workingFolder;
        _context       = context;
        _workspaces    = workspaces;

        var canSend = this.WhenAnyValue(x => x.Draft, x => x.IsStarting, (draft, starting) => !starting && !string.IsNullOrWhiteSpace(draft));
        SendCommand            = ReactiveCommand.CreateFromTask(SendAsync, canSend);
        NewConversationCommand = ReactiveCommand.CreateFromTask(EndConversationAsync);
    }

    public string Draft {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public bool IsStarting {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? Error {
        get;
        private set {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => Error is not null;

    /// <summary>The driver agent's session, once the first message has started it.</summary>
    public WorkspaceViewModel? Conversation {
        get;
        private set {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasConversation));
            this.RaisePropertyChanged(nameof(ShowsStarter));
        }
    }

    public bool HasConversation => Conversation is not null;
    public bool ShowsStarter    => Conversation is null;

    public ReactiveCommand<Unit, Unit> SendCommand            { get; }
    public ReactiveCommand<Unit, Unit> NewConversationCommand { get; }

    async Task SendAsync() {
        Error = null;
        if (_daemonName() is not { Length: > 0 } daemon) {
            Error = "No daemon is running on this machine, so Driver has nowhere to start its agent.";
            return;
        }
        if (_workingFolder() is not { Length: > 0 } folder) {
            Error = "Driver needs a folder for its agent to work in: set KCAP_DRIVER_REPO to a git checkout.";
            return;
        }

        IsStarting = true;
        try {
            var prompt  = Prompt(_context(), Draft.Trim());
            var outcome = await _launch.StartAsync(new LaunchRequest(daemon, folder, "claude", prompt), _shutdown.Token);
            if (!outcome.Started || outcome.AgentId is not { } agentId) {
                Error = outcome.Unauthorized ? "Sign in to start Driver's agent." : outcome.Error ?? "The agent did not start.";
                return;
            }
            Draft        = "";
            Conversation = _workspaces?.Invoke(agentId);
        } catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
        } finally {
            IsStarting = false;
        }
    }

    async Task EndConversationAsync() {
        if (Conversation is { } open) {
            Conversation = null;
            await open.TeardownAsync();
        }
    }

    internal static string Prompt(string context, string message) =>
        $"""
         You are Driver, the assistant in the kcap desktop app's Driver view. The person watches their work items there:
         what needs them, what is in flight, and what their agents are doing. Answer from the state below and keep replies
         short. Before you change anything (start an agent, change a work item or part, file an issue) say what you would
         do and wait for a yes. Never merge a pull request.

         {context}

         The person says: {message}
         """;

    public void Dispose() {
        _shutdown.Cancel();
        _shutdown.Dispose();
        if (Conversation is { } open) _ = open.TeardownAsync();
    }
}
