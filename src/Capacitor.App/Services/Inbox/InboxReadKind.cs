namespace Capacitor.App.Services.Inbox;

/// <summary><see cref="Unavailable"/> is a server too old to serve the read: the bell hides rather than reporting a
/// failure.</summary>
public enum InboxReadKind { Ready, Unavailable, SignedOut, Failed }
