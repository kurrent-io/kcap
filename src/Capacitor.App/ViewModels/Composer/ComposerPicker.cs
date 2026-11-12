using System.Reactive.Linq;
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Composer;

/// Rows are <see cref="HostedAgentCommand"/>s or <see cref="FileMention"/>s. Contexts must arrive on the UI thread.
public sealed class ComposerPicker : ReactiveObject, IDisposable {
    readonly SlashCompletion _slash;
    readonly MentionCompletion _mention;
    readonly IDisposable _contexts;
    ComposerContext? _context;
    int _generation;
    IReadOnlyList<object> _rows = [];

    public ComposerPicker(IObservable<ComposerContext?> contexts, SlashCompletion slash, MentionCompletion mention) {
        _slash = slash;
        _mention = mention;
        _contexts = contexts.DistinctUntilChanged().Subscribe(Enter);
    }

    public IReadOnlyList<object> Rows {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    /// The command listing starts here rather than on the first "/", since a probe starts the harness.
    void Enter(ComposerContext? context) {
        _context = context;
        _slash.Reset();
        _mention.Reset();
        if (context is not null) _slash.Prefetch(context);
        Dismiss();
    }

    public async Task UpdateAsync(string text, int caret) {
        var generation = ++_generation;
        caret = Math.Clamp(caret, 0, text.Length);
        IReadOnlyList<object> rows = [];
        if (_context is { } at) {
            var commands = await _slash.RowsAsync(text, at);
            var files = await _mention.RowsAsync(text, caret, at);
            rows = commands ?? files ?? [];
        }
        if (generation == _generation) Rows = rows;
    }

    public void Dismiss() {
        _generation++;
        Rows = [];
    }

    public static (string Text, int Caret) Insert(object row, string text, int caret) => row switch {
        HostedAgentCommand command => SlashCompletion.Insert(command),
        FileMention file => MentionCompletion.Insert(file, text, Math.Clamp(caret, 0, text.Length)),
        _ => throw new ArgumentException($"not a picker row: {row.GetType().Name}", nameof(row)),
    };

    public void Dispose() => _contexts.Dispose();
}
