namespace Capacitor.App.ViewModels.Pages;

/// Which page to open. Value-equal, so opening the page already shown is a no-op.
public abstract record PageKey {
    public static readonly PageKey Sessions = new SessionsKey();
    public static readonly PageKey WorkItems = new WorkItemsKey();

    public sealed record SessionsKey : PageKey;
    public sealed record WorkItemsKey : PageKey;
    public sealed record WorkItem(string Id) : PageKey;
}
