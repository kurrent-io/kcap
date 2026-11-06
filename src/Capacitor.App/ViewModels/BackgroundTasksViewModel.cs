using System.Reactive;
using Avalonia.Collections;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The subagents and commands running in the background, as the composer band lists them: folded
/// to a count until opened, and folded again once the last one ends so new work never arrives open.
public sealed class BackgroundTasksViewModel : ReactiveObject, IDisposable {
    readonly AvaloniaList<RunRow> _rows = new();
    int _agents;
    int _commands;
    bool _isExpanded;

    public BackgroundTasksViewModel() {
        ToggleCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });
        OpenRunCommand = ReactiveCommand.Create<RunRow>(row => { if (row.CanOpen) RunOpened?.Invoke(row); });
    }

    public IAvaloniaReadOnlyList<RunRow> Rows => _rows;
    public int Count => _rows.Count;
    public bool HasRows => _rows.Count > 0;

    /// The count by kind, which the folded header carries only as a number.
    public string CountTip => (_agents, _commands) switch {
        (_, 0) => $"{Plural(_agents, "subagent")} running",
        (0, _) => $"{Plural(_commands, "command")} running",
        _      => $"{Plural(_agents, "subagent")} and {Plural(_commands, "command")} running",
    };

    public string AccessibleName => $"Background tasks, {CountTip}, {(IsExpanded ? "expanded" : "collapsed")}";
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    /// A subagent row the user chose to read; the workspace opens its transcript, as it does from the sidebar.
    public event Action<RunRow>? RunOpened;
    public ReactiveCommand<RunRow, Unit> OpenRunCommand { get; }

    public bool IsExpanded {
        get => _isExpanded;
        private set {
            if (_isExpanded == value) return;
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            this.RaisePropertyChanged(nameof(AccessibleName));
        }
    }

    internal void Refresh(IEnumerable<RunRow> rows) {
        StableRows.Sync(_rows, rows);
        if (!HasRows) IsExpanded = false;
        var commands = _rows.Count(row => row.IsShell);
        var agents = _rows.Count - commands;
        if (agents == _agents && commands == _commands) return;
        _agents = agents;
        _commands = commands;
        this.RaisePropertyChanged(nameof(Count));
        this.RaisePropertyChanged(nameof(HasRows));
        this.RaisePropertyChanged(nameof(CountTip));
        this.RaisePropertyChanged(nameof(AccessibleName));
    }

    static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    public void Dispose() {
        ToggleCommand.Dispose();
        OpenRunCommand.Dispose();
    }
}
