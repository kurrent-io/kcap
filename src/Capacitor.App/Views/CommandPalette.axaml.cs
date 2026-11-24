using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

public sealed partial class CommandPalette : UserControl, IDisposable {
    readonly CommandPaletteEntry[] _entries = [];
    readonly CompositeDisposable _watch = new();
    readonly List<Button> _rows = [];
    (CommandPaletteEntry Entry, string Title)[]? _results;
    Button? _selected;
    bool _refreshQueued;

    public CommandPalette() => InitializeComponent();

    internal CommandPalette(IEnumerable<CommandPaletteEntry> entries) : this() {
        _entries = entries.ToArray();
        Refresh();
        foreach (var entry in _entries) {
            entry.Command.CanExecuteChanged += OnCanExecuteChanged;
            _watch.Add(Disposable.Create(() => entry.Command.CanExecuteChanged -= OnCanExecuteChanged));
        }
        var sources = _entries.Select(entry => entry.Source).OfType<Button>();
        foreach (var visual in sources.SelectMany(button => button.GetSelfAndVisualAncestors()).Distinct()) {
            visual.PropertyChanged += OnSourceChanged;
            _watch.Add(Disposable.Create(() => visual.PropertyChanged -= OnSourceChanged));
        }
        AddHandler(KeyDownEvent, (_, e) => {
            if (e.Key != Key.Tab) e.Handled = true;
        }, RoutingStrategies.Bubble);
    }

    internal TextBox QueryField => Query;
    internal event Action? Closed;

    void OnCanExecuteChanged(object? sender, EventArgs e) => QueueRefresh();

    void OnSourceChanged(object? sender, AvaloniaPropertyChangedEventArgs e) {
        if (e.Property == IsVisibleProperty || e.Property == ContentControl.ContentProperty || e.Property == IsEffectivelyEnabledProperty
            || e.Property == Button.CommandProperty || e.Property == Button.CommandParameterProperty) QueueRefresh();
    }

    void QueueRefresh() {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() => {
            _refreshQueued = false;
            if (!_watch.IsDisposed) Refresh();
        });
    }

    void OnQueryChanged(object? sender, TextChangedEventArgs e) => Refresh(resetSelection: true);
    void OnCloseClick(object? sender, RoutedEventArgs e) => Closed?.Invoke();
    void OnBackdropPressed(object? sender, PointerPressedEventArgs e) {
        e.Handled = true;
        Closed?.Invoke();
    }

    void Refresh(bool resetSelection = false) {
        var terms = (Query.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = _entries.Where(entry => entry.IsAvailable)
            .Select(entry => (Entry: entry, Title: entry.Title))
            .Where(match => terms.All(term => match.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || match.Entry.Scope.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (_results is not null && matches.SequenceEqual(_results) && !resetSelection) return;

        var previous = resetSelection ? null : _selected?.DataContext;
        var rowFocused = _rows.Any(row => row.IsFocused);
        _results = matches;
        _selected = null;
        _rows.Clear();
        Results.Children.Clear();
        foreach (var group in matches.GroupBy(match => match.Entry.Scope)) {
            var heading = SecondaryLabel(group.Key);
            heading.Margin = new Thickness(8, 6, 8, 2);
            AutomationProperties.SetHeadingLevel(heading, 2);
            Results.Children.Add(heading);
            foreach (var match in group) AddRow(match.Entry, match.Title);
        }

        var selected = _rows.FirstOrDefault(row => ReferenceEquals(row.DataContext, previous)) ?? _rows.FirstOrDefault();
        if (selected is not null) Select(selected);
        if (rowFocused && selected?.Focus(NavigationMethod.Tab) != true) Query.Focus();
        var empty = matches.Length == 0;
        if (empty && !EmptyResults.IsVisible) LiveAnnouncer.Polite(this, EmptyResults.Text!);
        EmptyResults.IsVisible = empty;
    }

    void AddRow(CommandPaletteEntry entry, string title) {
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        content.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        if (entry.Shortcut is { } shortcut) {
            var hint = SecondaryLabel(shortcut);
            Grid.SetColumn(hint, 1);
            content.Children.Add(hint);
        }
        var row = new Button {
            Content = content, DataContext = entry, Padding = new Thickness(8, 6), MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        row.Classes.Add("kcapGhost");
        row.Classes.Add("paletteResult");
        AutomationProperties.SetName(row, title);
        if (entry.Shortcut is { } key) AutomationProperties.SetAcceleratorKey(row, key);
        row.GotFocus += (_, _) => Select(row);
        row.Click += (_, _) => Run(entry);
        _rows.Add(row);
        Results.Children.Add(row);
    }

    static TextBlock SecondaryLabel(string text) {
        var label = new TextBlock { Text = text, FontSize = TypeScale.Small, VerticalAlignment = VerticalAlignment.Center };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("KcapMutedBrush"));
        return label;
    }

    void Select(Button row) {
        _selected?.Classes.Remove("highlighted");
        _selected = row;
        row.Classes.Add("highlighted");
    }

    internal void HandleKey(KeyEventArgs e) {
        if (e.KeyModifiers != KeyModifiers.None || Query.IsFocused && !string.IsNullOrEmpty(TextBoxPresenters.Of(Query)?.PreeditText)) return;
        switch (e.Key) {
            case Key.Escape:
                Closed?.Invoke();
                break;
            case Key.Up or Key.Down:
                MoveSelection(e.Key == Key.Down ? 1 : -1);
                break;
            case Key.Enter when Query.IsFocused:
                if (_selected?.DataContext is CommandPaletteEntry entry) Run(entry);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void MoveSelection(int step) {
        if (_rows.Count == 0) return;
        var index = _selected is null ? 0 : (_rows.IndexOf(_selected) + step + _rows.Count) % _rows.Count;
        var row = _rows[index];
        Select(row);
        row.BringIntoView();
        if (Query.IsFocused) LiveAnnouncer.Polite(this, AutomationProperties.GetName(row)!);
        else row.Focus(NavigationMethod.Tab);
    }

    void Run(CommandPaletteEntry entry) {
        if (!entry.IsAvailable) {
            Refresh();
            return;
        }
        var parameter = entry.Parameter;
        Closed?.Invoke();
        if (entry.IsAvailable) entry.Command.Execute(parameter);
    }

    public void Dispose() => _watch.Dispose();
}
