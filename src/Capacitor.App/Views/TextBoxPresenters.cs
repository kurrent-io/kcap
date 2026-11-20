using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;

namespace Capacitor.App.Views;

/// Each TextBox's PART_TextPresenter, cached as its template applies so a key handler never walks
/// the tree. Only text boxes templated after `Track` runs are known.
static class TextBoxPresenters {
    static readonly ConditionalWeakTable<TextBox, TextPresenter> Presenters = new();
    static int _tracking;

    public static void Track() {
        if (Interlocked.Exchange(ref _tracking, 1) == 1) return;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<TextBox>((box, e) => {
            if (e.NameScope.Find<TextPresenter>("PART_TextPresenter") is { } presenter) Presenters.AddOrUpdate(box, presenter);
            else Presenters.Remove(box);
        });
    }

    public static TextPresenter? Of(TextBox? box) =>
        box is not null && Presenters.TryGetValue(box, out var presenter) ? presenter : null;
}
