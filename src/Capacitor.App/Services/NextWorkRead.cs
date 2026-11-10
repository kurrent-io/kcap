namespace Capacitor.App.Services;

public sealed record NextWorkRead<T>(NextWorkReadKind Kind, T? Value = null) where T : class {
    public static readonly NextWorkRead<T> Unavailable = new(NextWorkReadKind.Unavailable);
    public static readonly NextWorkRead<T> SignedOut   = new(NextWorkReadKind.SignedOut);
    public static readonly NextWorkRead<T> Failed      = new(NextWorkReadKind.Failed);
}
