using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Which launcher notice shows: only one, and the next takes its place once it clears. A problem the user
/// can act on comes before anything still in progress, and that before a note. Among equals the local stack
/// leads, since nothing connects without it, then the local server, then the daemon and server connection.
/// A local server card folds into a local stack card that is not a note, which already covers the server.
/// While an update is still finishing, its card is the only one: the others would describe the local server
/// and the daemon coming back, which is what the update is doing.
/// History follows infrastructure on ties; its nonblocking attention follows progress and precedes notes.
public sealed class LauncherNotices : ReactiveObject, IDisposable {
    public enum Kind { Problem, Progress, Attention, Note }

    readonly BehaviorSubject<(bool Visible, Kind Kind)> _stack = new((false, Kind.Note));
    readonly BehaviorSubject<(bool Visible, Kind Kind)> _server = new((false, Kind.Problem));
    readonly BehaviorSubject<(bool Visible, Kind Kind)> _connection = new((false, Kind.Problem));
    readonly BehaviorSubject<(bool Visible, Kind Kind)> _history = new((false, Kind.Note));
    readonly SerialDisposable _historyTracking = new();
    bool _disposed;
    long _historyVersion;
    readonly BehaviorSubject<bool> _update = new(false);
    readonly CompositeDisposable _disposables = new();

    readonly ObservableAsPropertyHelper<bool> _showStack;
    readonly ObservableAsPropertyHelper<bool> _showServer;
    readonly ObservableAsPropertyHelper<bool> _showConnection;
    readonly ObservableAsPropertyHelper<bool> _showUpdate;
    readonly ObservableAsPropertyHelper<bool> _showHistory;

    public LauncherNotices() {
        _historyTracking.DisposeWith(_disposables);
        var top = _stack.CombineLatest(_server, _connection, _update, _history, (stack, server, connection, update, history) => {
            if (update) return 4;
            if (stack.Visible && stack.Kind != Kind.Note) server = server with { Visible = false };
            (bool Visible, Kind Kind)[] cards = [stack, server, connection, history];
            return Enumerable.Range(0, cards.Length).Where(i => cards[i].Visible)
                .OrderBy(i => cards[i].Kind).ThenBy(i => i).DefaultIfEmpty(-1).First();
        }).Publish().RefCount();

        _showHistory = top.Select(i => i == 3).ToProperty(this, x => x.ShowHistory).DisposeWith(_disposables);
        _showStack = top.Select(i => i == 0).ToProperty(this, x => x.ShowStack).DisposeWith(_disposables);
        _showServer = top.Select(i => i == 1).ToProperty(this, x => x.ShowServer).DisposeWith(_disposables);
        _showConnection = top.Select(i => i == 2).ToProperty(this, x => x.ShowConnection).DisposeWith(_disposables);
        _showUpdate = top.Select(i => i == 4).ToProperty(this, x => x.ShowUpdate).DisposeWith(_disposables);
    }

    public bool ShowHistory => _showHistory.Value;
    public bool ShowStack => _showStack.Value;
    public bool ShowServer => _showServer.Value;
    public bool ShowConnection => _showConnection.Value;
    public bool ShowUpdate => _showUpdate.Value;

    public IDisposable TrackHistory(IObservable<(bool Visible, Kind Kind)> history) {
        if (_disposed) return Disposable.Empty;
        var version = ++_historyVersion;
        _historyTracking.Disposable = history.Subscribe(value => {
            if (!_disposed && version == _historyVersion) _history.OnNext(value);
        });
        return Disposable.Create(() => {
            if (_disposed || version != _historyVersion) return;
            ++_historyVersion;
            _historyTracking.Disposable = Disposable.Empty;
            _history.OnNext((false, Kind.Note));
        });
    }

    public void TrackStack(IObservable<(bool Visible, Kind Kind)> stack) => stack.Subscribe(_stack.OnNext).DisposeWith(_disposables);
    public void TrackServer(IObservable<(bool Visible, Kind Kind)> server) => server.Subscribe(_server.OnNext).DisposeWith(_disposables);
    public void TrackConnection(IObservable<(bool Visible, Kind Kind)> connection) => connection.Subscribe(_connection.OnNext).DisposeWith(_disposables);
    public void TrackUpdate(IObservable<bool> finishing) => finishing.Subscribe(_update.OnNext).DisposeWith(_disposables);

    public void Dispose() {
        _disposed = true;
        _disposables.Dispose();
        _stack.Dispose();
        _server.Dispose();
        _connection.Dispose();
        _update.Dispose();
        _history.Dispose();
    }
}
