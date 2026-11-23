namespace Capacitor.App.ViewModels.Pages;

/// A slice's box in the graph, placed by its top left corner.
public sealed record WorkItemPageGraphNode(WorkItemPagePart Part, double X, double Y) {
    public const double Width = 220, Height = 52;
}
