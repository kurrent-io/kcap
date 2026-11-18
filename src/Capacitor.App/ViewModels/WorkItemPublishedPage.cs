namespace Capacitor.App.ViewModels;

/// A Page row of a work item. It opens at the URL the server listed.
public sealed record WorkItemPublishedPage(string Title, string Meta, string Url);
