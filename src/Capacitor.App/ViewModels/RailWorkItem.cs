namespace Capacitor.App.ViewModels;

/// An open work item of the picked project: its title, the parts done out of all, the same for every viewer, and
/// the part title of each session in it the viewer can see, keyed by session id.
public sealed record RailWorkItem(string Id, string Title, int PartsDone, int PartsTotal, IReadOnlyDictionary<string, string> PartBySession) {
    public const string NoPart = "No part";
}
