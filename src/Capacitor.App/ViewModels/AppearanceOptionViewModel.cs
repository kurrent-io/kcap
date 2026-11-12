using System.Reactive;
using Avalonia.Media;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One theme or font in the Appearance tab. A font option previews its label in its own face.
public sealed class AppearanceOptionViewModel : ReactiveObject {
    bool _isSelected;

    public AppearanceOptionViewModel(string id, string label, FontFamily? preview, Func<Task> select) {
        Id = id;
        Label = label;
        Preview = preview;
        SelectCommand = ReactiveCommand.CreateFromTask(select);
    }

    public string Id { get; }
    public string Label { get; }
    public FontFamily? Preview { get; }
    public ReactiveCommand<Unit, Unit> SelectCommand { get; }

    public bool IsSelected {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}
