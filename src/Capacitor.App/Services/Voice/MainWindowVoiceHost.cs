using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows.Input;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.Services.Voice;

/// Voice control over the main window. The window can be closed and rebuilt, so each call reads
/// the view model it shows now rather than holding one.
public sealed class MainWindowVoiceHost(
        Func<MainWindowViewModel?> currentWindow, Action showWindow, UiScaleSetting? uiScale) : IVoiceHost {
    static readonly TimeSpan OpenWait = TimeSpan.FromMinutes(1);
    static readonly TimeSpan TurnLimit = TimeSpan.FromMinutes(30);
    // A turn with no status change after its last reply still ends: some channels never report one.
    static readonly TimeSpan QuietEnd = TimeSpan.FromSeconds(6);

    MainWindowViewModel? Window => currentWindow();
    HomeViewModel? Home => Window?.Home;
    ISessionWorkspace? Workspace => Window?.CurrentWorkspace;
    ChatTabViewModel? Chat => ChatOf(Workspace);

    public void ShowWindow() => showWindow();

    public IReadOnlyList<VoiceSession> Sessions {
        get {
            if (Window?.Rail is not { } rail) return [];
            var open = Workspace;
            return rail.Groups.SelectMany(group => group.Sessions).Select(session => new VoiceSession(
                session.Key, session.Primary ?? (session.HasVendor ? session.Vendor : "Untitled session"), session.Status.Label,
                open is not null && open.AgentId == session.Id && (open is RemoteSessionViewModel) == session.IsRemote)).ToList();
        }
    }

    public VoiceSession? OpenSession => Sessions.FirstOrDefault(s => s.IsOpen)
        ?? (Workspace is { } workspace ? new VoiceSession(workspace.AgentId, TitleOf(workspace), "", true) : null);

    public bool Open(string agentId) {
        var session = Window?.Rail?.Groups.SelectMany(group => group.Sessions).FirstOrDefault(s => s.Key == agentId);
        return session is not null && Run(session.OpenCommand);
    }

    public void GoHome() => Window?.CloseWorkspace();

    public bool ShowTab(VoiceTab tab) => Workspace switch {
        WorkspaceViewModel local => tab switch {
            VoiceTab.Terminal => local.ShowsTerminalTab && Run(local.ShowTerminalCommand),
            VoiceTab.PullRequest => local.ShowsPullRequestTab && Run(local.ShowPullRequestCommand),
            VoiceTab.Artefacts => local.ShowsArtefactsTab && Run(local.ShowArtefactsCommand),
            _ => Run(local.ShowChatCommand),
        },
        RemoteSessionViewModel remote => tab switch {
            VoiceTab.Terminal => remote.ShowsTerminalTab && Run(remote.ShowTerminalCommand),
            VoiceTab.PullRequest => remote.ShowsPullRequestTab && Run(remote.ShowPullRequestCommand),
            VoiceTab.Artefacts => remote.ShowsArtefactsTab && Run(remote.ShowArtefactsCommand),
            _ => Run(remote.ShowChatCommand),
        },
        _ => false,
    };

    public async Task<string?> SendPromptAsync(string text) {
        if (Chat is not { } chat) return "The session isn't ready for messages yet.";
        ShowTab(VoiceTab.Chat);
        var draft = chat.ComposerText;
        chat.ComposerText = text;
        if (!await chat.SendCommand.CanExecute.FirstAsync()) {
            var hint = chat.ComposerHint;
            chat.ComposerText = draft;
            return string.IsNullOrWhiteSpace(hint) ? "The session can't take a message right now." : hint;
        }
        await chat.SendCommand.Execute();
        if (chat.ComposerText == text) {
            chat.ComposerText = draft;
            return "The message wasn't accepted.";
        }
        if (draft.Length > 0 && chat.ComposerText.Length == 0) chat.ComposerText = draft;
        return null;
    }

    public bool Dictate(string text) {
        if (Chat is { } chat) {
            chat.ComposerText = chat.ComposerText.Length == 0 ? text : $"{chat.ComposerText.TrimEnd()} {text}";
            return true;
        }
        if (Workspace is null && Home is { } home) {
            home.Goal = home.Goal.Length == 0 ? text : $"{home.Goal.TrimEnd()} {text}";
            return true;
        }
        return false;
    }

    public async Task<string?> SendDraftAsync() {
        if (Chat is { } chat) {
            if (string.IsNullOrWhiteSpace(chat.ComposerText)) return "The message box is empty.";
            if (!await chat.SendCommand.CanExecute.FirstAsync()) return "The session can't take a message right now.";
            await chat.SendCommand.Execute();
            return null;
        }
        if (Workspace is null && Home is { Goal.Length: > 0 } home) return await StartSessionAsync(home.Goal);
        return "There's nothing to send.";
    }

    public async Task<string?> StartSessionAsync(string prompt) {
        if (Home is not { } home) return "The launcher isn't available.";
        GoHome();
        home.Goal = prompt;
        if (!await home.StartCommand.CanExecute.FirstAsync())
            return home.StartError ?? "A session can't start right now. Check the launcher.";
        await home.StartCommand.Execute();
        return home.StartError;
    }

    public async Task<string?> StartSessionAsync(string prompt, VoicePermissionLevel level) {
        if (Home is not { } home) return "The launcher isn't available.";
        var (mode, sandbox) = (home.SelectedPermissionMode, home.SelectedCodexSandbox);
        if (Apply(home, level) is null) return $"{HarnessName(home)} has no permission setting to change.";
        try {
            return await StartSessionAsync(prompt);
        } finally {
            home.SelectedPermissionMode = mode;
            home.SelectedCodexSandbox = sandbox;
        }
    }

    public async Task<string?> SetPermissionsAsync(VoicePermissionLevel level) {
        if (Home is not { } home) return null;
        if (Vendor(home) == "claude") {
            var token = ClaudeMode(level);
            await home.ChoosePermissionModeAsync(token);
            return HostedHarnessCatalog.PermissionModeLabelFor(token);
        }
        return Apply(home, level);
    }

    // The setting's name, or null when the chosen agent has no permission setting.
    static string? Apply(HomeViewModel home, VoicePermissionLevel level) {
        switch (Vendor(home)) {
            case "claude":
                home.SelectedPermissionMode = ClaudeMode(level);
                return HostedHarnessCatalog.PermissionModeLabelFor(home.SelectedPermissionMode);
            case "codex":
                home.SelectedCodexSandbox = level switch {
                    VoicePermissionLevel.Manual => "read-only",
                    VoicePermissionLevel.Bypass => "danger-full-access",
                    _ => "workspace-write",
                };
                return HostedHarnessCatalog.CodexSandboxes.FirstOrDefault(c => c.Token == home.SelectedCodexSandbox)?.Label;
            default:
                return null;
        }
    }

    static string Vendor(HomeViewModel home) => home.SelectedVendor.ToLowerInvariant();

    static string ClaudeMode(VoicePermissionLevel level) => level switch {
        VoicePermissionLevel.AcceptEdits => Capacitor.Cli.Core.Harness.Claude.ClaudePermissionModes.AcceptEdits,
        VoicePermissionLevel.Auto => Capacitor.Cli.Core.Harness.Claude.ClaudePermissionModes.Auto,
        VoicePermissionLevel.Bypass => Capacitor.Cli.Core.Harness.Claude.ClaudePermissionModes.BypassPermissions,
        _ => Capacitor.Cli.Core.Harness.Claude.ClaudePermissionModes.Manual,
    };

    static string HarnessName(HomeViewModel home) =>
        home.Harnesses.FirstOrDefault(h => h.Vendor == home.SelectedVendor)?.Label ?? home.SelectedVendor;

    public async Task<string?> SelectRepositoryAsync(string query) {
        if (Home is not { } home) return null;
        var repositories = await home.ListRepositoriesAsync();
        var candidates = repositories.Select(r => new VoiceSession(r.RepoPath, PlatformPaths.Leaf(r.RepoPath), "", r.Selected)).ToList();
        if (SessionMatcher.Best(query, candidates) is not { } match) return null;
        await home.SelectRepositoryAsync(match.AgentId);
        return match.Title;
    }

    public async Task<string?> SelectHarnessAsync(string query) {
        if (Home is not { } home) return null;
        var available = home.Harnesses.Where(h => h.Available)
            .Select(h => new VoiceSession(h.Vendor, h.Label, "", h.Vendor == home.SelectedVendor)).ToList();
        if (SessionMatcher.Best(query, available) is not { } match) return null;
        await home.ChooseHarnessAsync(match.AgentId);
        return match.Title;
    }

    public string LauncherSummary {
        get {
            if (Home is not { } home) return "";
            var harness = home.Harnesses.FirstOrDefault(h => h.Vendor == home.SelectedVendor)?.Label ?? home.SelectedVendor;
            var repo = string.IsNullOrEmpty(home.SelectedRepoPath) ? "no repository" : PlatformPaths.Leaf(home.SelectedRepoPath);
            var permissions = Vendor(home) switch {
                "claude" when home.SelectedPermissionMode != Capacitor.Cli.Core.Harness.Claude.ClaudePermissionModes.Manual =>
                    $" Permissions: {HostedHarnessCatalog.PermissionModeLabelFor(home.SelectedPermissionMode).ToLowerInvariant()}.",
                "codex" => HostedHarnessCatalog.CodexSandboxes.FirstOrDefault(c => c.Token == home.SelectedCodexSandbox) is { } sandbox
                    ? $" Sandbox: {sandbox.Label.ToLowerInvariant()}." : "",
                _ => "",
            };
            return (harness.Length > 0 ? $"In {repo}, with {harness}." : $"In {repo}.") + permissions;
        }
    }

    public bool StopAgent() => Workspace switch {
        WorkspaceViewModel local => Run(local.StopCommand),
        RemoteSessionViewModel remote => Run(remote.StopCommand),
        _ => false,
    };

    public bool Interrupt() => Chat is { ShowsInterrupt: true } chat && Run(chat.InterruptCommand);

    public string? PendingPermission => Chat?.PendingCards.OfType<PermissionCardViewModel>().FirstOrDefault() is { } card
        ? PermissionText(card) : null;

    public async Task<bool> AnswerPermissionAsync(PermissionAnswer answer) {
        if (Chat?.PendingCards.OfType<PermissionCardViewModel>().FirstOrDefault() is not { } card) return false;
        var command = answer switch {
            PermissionAnswer.Deny => card.DenyCommand,
            PermissionAnswer.AllowAlways when card.ShowsAllowAlways => card.AllowAlwaysCommand,
            _ => card.AllowCommand,
        };
        if (!await command.CanExecute.FirstAsync()) return false;
        await command.Execute();
        return card.ErrorText is null;
    }

    public string? LastReply => Chat?.Items.OfType<AssistantTextItem>().LastOrDefault()?.Text;

    public string StatusSummary {
        get {
            if (Window is null) return "The window isn't open.";
            var count = Sessions.Count;
            var parts = new List<string> { count == 1 ? "1 session." : $"{count} sessions." };
            if (Workspace is { } open) {
                var status = Chat?.AgentStatus.Label;
                parts.Add(string.IsNullOrEmpty(status) ? $"{TitleOf(open)} is open." : $"{TitleOf(open)} is {status.ToLowerInvariant()}.");
                if (PendingPermission is { } pending) parts.Add($"It's waiting for permission: {pending}.");
            } else {
                parts.Add($"The launcher is open. {LauncherSummary}");
            }
            return string.Join(" ", parts);
        }
    }

    public void OpenSettings() {
        if (Window is { } window) Run(window.OpenSettingsCommand);
    }

    public bool Refresh() => Window is { } window && Run(window.RefreshWorkCommand);

    public void Zoom(int direction) {
        if (uiScale is null) return;
        var next = direction switch {
            > 0 => UiScale.Larger(uiScale.Current),
            < 0 => UiScale.Smaller(uiScale.Current),
            _ => UiScale.Default,
        };
        _ = uiScale.SetAsync(next);
    }

    public IObservable<VoiceNarration> WatchNextTurn() {
        if (Window is not { } window) return Observable.Empty<VoiceNarration>();
        return Observable.Switch(window.WhenAnyValue(x => x.CurrentWorkspace).Select(ChatsOf))
            .Where(chat => chat is not null)
            .Take(1)
            .Timeout(OpenWait, Observable.Empty<ChatTabViewModel?>())
            .SelectMany(chat => TurnOf(chat!))
            .Timeout(TurnLimit, Observable.Empty<VoiceNarration>());
    }

    static IObservable<VoiceNarration> TurnOf(ChatTabViewModel chat) => Observable.Create<VoiceNarration>(observer => {
        var subscriptions = new CompositeDisposable();
        var replyAdded = new Subject<Unit>().DisposeWith(subscriptions);
        string? latest = null;
        var sawWork = false;
        var finished = false;

        void Finish(VoiceNarration? last) {
            if (finished) return;
            finished = true;
            if (last is not null) observer.OnNext(last);
            observer.OnCompleted();
        }

        bool Ends(AgentStatusPresentation status) => !status.IsInFlight && status.Kind is not (AgentStatusKind.NeedsYou or AgentStatusKind.Answer);

        NotifyCollectionChangedEventHandler items = (_, e) => {
            if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null) return;
            foreach (var item in e.NewItems) {
                if (item is AssistantTextItem { Text.Length: > 0 } reply) latest = reply.Text;
            }
            if (latest is not null) replyAdded.OnNext(Unit.Default);
        };
        chat.Items.CollectionChanged += items;
        Disposable.Create(() => chat.Items.CollectionChanged -= items).DisposeWith(subscriptions);

        NotifyCollectionChangedEventHandler cards = (_, e) => {
            if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null) return;
            foreach (var card in e.NewItems) {
                if (AttentionText(card) is { } text) observer.OnNext(new VoiceNarration.Attention(text));
            }
        };
        ((INotifyCollectionChanged)chat.PendingCards).CollectionChanged += cards;
        Disposable.Create(() => ((INotifyCollectionChanged)chat.PendingCards).CollectionChanged -= cards).DisposeWith(subscriptions);

        chat.WhenAnyValue(c => c.AgentStatus).Subscribe(status => {
            if (status.IsInFlight) {
                sawWork = true;
                return;
            }
            if (!sawWork || !Ends(status)) return;
            if (latest is not null) Finish(new VoiceNarration.Reply(latest));
            else if (status.Kind == AgentStatusKind.Failed) Finish(new VoiceNarration.Attention("The agent stopped with an error."));
        }).DisposeWith(subscriptions);

        replyAdded.Throttle(QuietEnd).ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => {
            if (latest is not null && Ends(chat.AgentStatus)) Finish(new VoiceNarration.Reply(latest));
        }).DisposeWith(subscriptions);

        return subscriptions;
    });

    static IObservable<ChatTabViewModel?> ChatsOf(ISessionWorkspace? workspace) => workspace switch {
        WorkspaceViewModel local => local.WhenAnyValue(x => x.Chat),
        RemoteSessionViewModel remote => Observable.Return<ChatTabViewModel?>(remote.Chat),
        _ => Observable.Return<ChatTabViewModel?>(null),
    };

    static ChatTabViewModel? ChatOf(ISessionWorkspace? workspace) => workspace switch {
        WorkspaceViewModel local => local.Chat,
        RemoteSessionViewModel remote => remote.Chat,
        _ => null,
    };

    static string TitleOf(ISessionWorkspace workspace) => workspace switch {
        WorkspaceViewModel { Title.Length: > 0 } local => local.Title,
        RemoteSessionViewModel { Title.Length: > 0 } remote => remote.Title,
        _ => "The session",
    };

    static string? AttentionText(object card) => card switch {
        PermissionCardViewModel permission => $"Permission needed. {PermissionText(permission)}. Say allow, always allow, or deny.",
        QuestionCardViewModel { Questions: [var first, ..] } => $"The agent asks: {first.Text} Answer on screen, or say your answer.",
        AcpQuestionCardViewModel question => $"The agent asks: {question.Prompt} Answer on screen, or say your answer.",
        _ => null,
    };

    static string PermissionText(PermissionCardViewModel card) {
        var detail = card.Detail.Length > 160 ? card.Detail[..160] + "…" : card.Detail;
        return detail.Length == 0 ? card.ToolName : $"{card.ToolName}: {detail}";
    }

    static bool Run(ICommand command) {
        if (!command.CanExecute(null)) return false;
        command.Execute(null);
        return true;
    }
}
