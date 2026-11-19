namespace Capacitor.App.ViewModels.Pages;

/// A Page row of a work item. It opens at the URL the server listed.
public sealed record WorkItemPublishedPage(string ArtefactId, string Title, string Meta, string Url);
