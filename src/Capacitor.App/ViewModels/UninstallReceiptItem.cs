namespace Capacitor.App.ViewModels;

/// One line of what a finished uninstall removed. <see cref="Done"/> is false for a note about
/// something left behind or not removed yet.
public sealed record UninstallReceiptItem(string Text, bool Done = true);
