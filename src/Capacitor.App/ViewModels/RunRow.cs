using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One subagent or background command of a session. Outcome is the row's own state;
/// State is what it presents, which reads Stopped for a still-running row once the session is over.
public sealed class RunRow : ReactiveObject {
    bool _isBackground;
    string? _agentId;
    bool _outcomeUnknown;
    bool _endedByStop;
    /// With no report of its own, a run that began before the latest restart is running only if it
    /// waited after that restart. Each is the latest heard, so the order they land in cannot matter.
    DateTimeOffset? _restartedAt;
    DateTimeOffset? _waitedAt;
    bool _openable = true;
    RunState _state = RunState.Running;
    string _stateText = "";
    string _elapsedText = "";

    public RunRow(string callId, string name, string description, DateTimeOffset startedAt, RunKind kind = RunKind.Agent) {
        CallId = callId;
        Name = name;
        Description = description;
        StartedAt = startedAt;
        Kind = kind;
    }

    public string CallId { get; }
    public string Name { get; }
    public string Description { get; }
    public DateTimeOffset StartedAt { get; }
    public RunKind Kind { get; }
    public bool IsShell => Kind == RunKind.Shell;
    public DateTimeOffset? EndedAt { get; private set; }
    public RunState Outcome { get; private set; } = RunState.Running;
    public bool IsEnded => Outcome != RunState.Running;

    public bool IsBackground { get => _isBackground; private set => this.RaiseAndSetIfChanged(ref _isBackground, value); }

    /// The subagent's id, which its own transcript is filed under; null for a command, or until it is known.
    public string? AgentId {
        get => _agentId;
        private set {
            if (_agentId == value) return;
            this.RaiseAndSetIfChanged(ref _agentId, value);
            this.RaisePropertyChanged(nameof(CanOpen));
            this.RaisePropertyChanged(nameof(UnopenableReason));
        }
    }

    /// Whether the subagent's own transcript can be opened from this row.
    public bool CanOpen => Kind == RunKind.Agent && AgentId is not null && _openable;

    /// Why a subagent row does not open; null for one that does, and for a command, which never opens.
    /// An id is only ever taken from a record naming this call or an unambiguous start, never guessed,
    /// so an ended row without one stays shut and says so.
    public string? UnopenableReason => Kind != RunKind.Agent || CanOpen ? null
        : !_openable ? "Subagent transcripts can't be opened for this session."
        : IsRunning ? "Opens once kcap knows which transcript this subagent writes."
        : "kcap has no record of which subagent transcript this call started, so it can't be opened.";

    /// Set by the owning lane: false where it has no way to read a subagent's transcript.
    internal bool Openable {
        get => _openable;
        set {
            if (_openable == value) return;
            _openable = value;
            this.RaisePropertyChanged(nameof(CanOpen));
            this.RaisePropertyChanged(nameof(UnopenableReason));
        }
    }

    internal void Identify(string agentId) {
        if (Kind == RunKind.Agent) AgentId = agentId;
    }

    public RunState State {
        get => _state;
        private set {
            if (_state == value) return;
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(IsRunning));
            this.RaisePropertyChanged(nameof(IsFailed));
            this.RaisePropertyChanged(nameof(StateWord));
            this.RaisePropertyChanged(nameof(UnopenableReason));
        }
    }

    public string StateText { get => _stateText; private set => this.RaiseAndSetIfChanged(ref _stateText, value); }

    /// The duration alone; empty for a row the session's end stopped.
    public string ElapsedText { get => _elapsedText; private set => this.RaiseAndSetIfChanged(ref _elapsedText, value); }

    public bool HasDescription => Description.Length > 0;

    public bool IsRunning => State == RunState.Running;
    public bool IsFailed  => State == RunState.Failed;

    /// The row's state without its ticking duration, so a screen reader's name for it changes only when the run does.
    public string StateWord => State switch {
        RunState.Running when IsBackground => "running in background",
        RunState.Running                   => "running",
        RunState.Failed                    => "failed",
        RunState.Stopped                   => "stopped",
        _                                  => "done",
    };

    internal void MarkBackground() {
        IsBackground = true;
        this.RaisePropertyChanged(nameof(StateWord));
    }

    /// An end with an outcome is final. One without reads as done until one with an outcome
    /// arrives, and the earliest end dates the row: the server stamps a stop when it heard it,
    /// which an import or a spooled hook puts long after the run ended, and a resume ends the run
    /// before it when it is heard, which can be after that run's own notification is dated.
    internal void End(RunState? outcome, DateTimeOffset at) {
        if (IsEnded && !_outcomeUnknown) return;
        EndedAt = EndedAt is { } bare && bare < at ? bare : at;
        Outcome = outcome ?? (IsEnded ? Outcome : RunState.Done);
        _outcomeUnknown = outcome is null;
        _endedByStop = false;
        IsCutOff = false;
    }

    internal DateTimeOffset? WaitedAt => _waitedAt;

    /// Ended by a restart rather than by anything the run said.
    internal bool IsCutOff { get; private set; }

    /// The process holding the run is gone, so it reads as stopped, but that is only a presumption: a
    /// report that still lands says how it really ended.
    internal void CutOff(DateTimeOffset at) {
        _restartedAt = Later(_restartedAt, at);
        if (IsEnded || _waitedAt > _restartedAt) return;
        End(RunState.Stopped, at);
        _outcomeUnknown = true;
        IsCutOff = true;
    }

    static DateTimeOffset Later(DateTimeOffset? known, DateTimeOffset at) => known > at ? known.Value : at;

    /// A stop record ends the row without an outcome, and alone among ends it can be taken back:
    /// a Claude agent also stops when it pauses on work of its own.
    internal void Stop(DateTimeOffset at) {
        if (IsEnded) return;
        End(null, at);
        _endedByStop = true;
    }

    /// A wait shows the run had not ended, so it takes back an end that only a stop record or a restart
    /// made: the pause's own stop, heard ahead of the notification. Past a restart only a wait dated
    /// after it can, since a notification can be written well after the time it carries.
    internal void Waited(DateTimeOffset at) {
        _waitedAt = Later(_waitedAt, at);
        if (!_endedByStop && !IsCutOff || _waitedAt <= _restartedAt) return;
        Outcome = RunState.Running;
        EndedAt = null;
        _outcomeUnknown = false;
        _endedByStop = false;
        IsCutOff = false;
    }

    internal void Present(bool sessionOver, DateTimeOffset now) {
        var stoppedBySession = sessionOver && !IsEnded;
        State = stoppedBySession ? RunState.Stopped : Outcome;
        // Nothing dates an end the session imposed, so that row shows no duration.
        var elapsed = (EndedAt ?? now) - StartedAt;
        ElapsedText = stoppedBySession ? "" : Duration(elapsed);
        StateText = stoppedBySession ? "stopped" : Text(State, IsBackground, elapsed);
    }

    // Background only matters while the run is live: it says the chat is not waiting on it.
    static string Text(RunState state, bool background, TimeSpan elapsed) => state switch {
        RunState.Running when background => $"running in background · {Duration(elapsed)}",
        RunState.Running => $"running · {Duration(elapsed)}",
        RunState.Failed  => $"failed · {Duration(elapsed)}",
        RunState.Stopped => $"stopped · {Duration(elapsed)}",
        _                     => Duration(elapsed),
    };

    internal static string Duration(TimeSpan elapsed) {
        var seconds = Math.Max(0, (long)elapsed.TotalSeconds);
        return seconds switch {
            < 60   => $"{seconds}s",
            < 3600 => $"{seconds / 60}m {seconds % 60:00}s",
            _      => $"{seconds / 3600}h {seconds % 3600 / 60:00}m",
        };
    }
}
