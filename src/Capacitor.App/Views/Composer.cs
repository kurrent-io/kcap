using Avalonia.Controls;
using Capacitor.App.ViewModels.Composer;

namespace Capacitor.App.Views;

/// A prompt card wired to the <see cref="IComposer"/> its text box shows: attachments by paste, drop and pick,
/// and the / and @ picker.
public sealed class Composer {
    AttachmentDropPaste? _attachments;

    Composer() { }

    internal Task? PendingIntakeForTesting => _attachments?.PendingIntakeForTesting;

    /// Call before the host adds its own KeyDown handlers to the input: the picker must take Enter first.
    /// Attachments follow the card's visual tree, so a view swapped out and back comes back with them.
    public static Composer Attach(Control card, TextBox input, Button? pick) {
        var composer = new Composer();
        IComposer? Current() => input.DataContext as IComposer;
        ComposerPickerPopup.Attach(input, () => Current()?.Picker);
        card.AttachedToVisualTree += (_, _) =>
            composer._attachments ??= AttachmentDropPaste.Attach(card, input, pick, Current, TimeProvider.System);
        card.DetachedFromVisualTree += (_, _) => {
            composer._attachments?.Dispose();
            composer._attachments = null;
        };

        return composer;
    }
}
