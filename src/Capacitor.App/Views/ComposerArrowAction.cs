namespace Capacitor.App.Views;

/// What a bare ↑/↓ in the composer does.
public enum ComposerArrowAction {
    /// The TextBox's own caret move.
    PassThrough,
    MoveToStart,
    MoveToEnd,
    RecallOlder,
    RecallNewer
}
