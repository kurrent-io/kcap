namespace Capacitor.App.ViewModels;

/// One read of a project's open work items; not available on a server without work items version 2.
public sealed record RailWorkItems(bool Available, IReadOnlyList<RailWorkItem> Items) {
    public static readonly RailWorkItems Unavailable = new(false, []);
}
