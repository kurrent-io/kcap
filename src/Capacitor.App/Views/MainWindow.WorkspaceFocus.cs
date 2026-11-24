using Avalonia.Controls;

namespace Capacitor.App.Views;

public partial class MainWindow {
    partial void CloseCommandPalette();
    partial void CheckCommandPaletteOpen(ref bool open);

    bool IsCommandPaletteOpen() {
        var open = false;
        CheckCommandPaletteOpen(ref open);
        return open;
    }

    void RestoreWorkspaceFocus(Control? previous) {
        if (previous is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true }
            && TopLevel.GetTopLevel(previous) == this && previous.Focus()) return;
        if (!PaneCycle.Move(this, 1)) Focus();
    }
}
