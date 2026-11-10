namespace Capacitor.App.ViewModels.NextWork;

/// <summary>The band read as a short list of things to do: <see cref="Waiting"/> where someone else waits on the
/// viewer, <see cref="Picks"/> from the viewer's own work, then the rest split by what they ask for.</summary>
public sealed record NextWorkFocus(
    IReadOnlyList<NextWorkFocusItem> Waiting,
    IReadOnlyList<NextWorkFocusItem> Picks,
    IReadOnlyList<NextWorkFocusItem> More,
    IReadOnlyList<NextWorkFocusItem> Notes,
    IReadOnlyList<NextWorkFocusItem> Quiet) {
    public static readonly NextWorkFocus Empty = new([], [], [], [], []);

    public int Total => Waiting.Count + Picks.Count + More.Count + Notes.Count + Quiet.Count;
}
