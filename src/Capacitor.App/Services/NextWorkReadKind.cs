namespace Capacitor.App.Services;

/// <summary><see cref="Unavailable"/> is a server without the next-work engine (or too old to serve this read): the
/// band hides rather than reporting a failure.</summary>
public enum NextWorkReadKind { Ready, Unavailable, SignedOut, Failed }
