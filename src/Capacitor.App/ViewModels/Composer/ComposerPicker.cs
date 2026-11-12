using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Composer;

/// Rows are <see cref="HostedAgentCommand"/>s or <see cref="FileMention"/>s.
public sealed class ComposerPicker(Func<ComposerContext?> context, SlashCompletion slash, MentionCompletion mention) : ReactiveObject {
    int _generation;
    ComposerContext? _context;
    IReadOnlyList<object> _rows = [];

    public IReadOnlyList<object> Rows {
        get => _rows;
        private set => this.RaiseAndSetIfChanged(ref _rows, value);
    }

    public async Task UpdateAsync(string text, int caret) {
        var generation = ++_generation;
        caret = Math.Clamp(caret, 0, text.Length);
        IReadOnlyList<object> rows = [];
        var current = context();
        if (current != _context) {
            slash.Reset();
            mention.Reset();
            _context = current;
        }
        if (current is { } at) {
            var commands = await slash.RowsAsync(text, at);
            var files = await mention.RowsAsync(text, caret, at);
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
}
