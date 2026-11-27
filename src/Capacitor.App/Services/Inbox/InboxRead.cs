namespace Capacitor.App.Services.Inbox;

public sealed record InboxRead<T>(InboxReadKind Kind, T? Value = null) where T : class {
    public static readonly InboxRead<T> Unavailable = new(InboxReadKind.Unavailable);
    public static readonly InboxRead<T> SignedOut   = new(InboxReadKind.SignedOut);
    public static readonly InboxRead<T> Failed      = new(InboxReadKind.Failed);
}
