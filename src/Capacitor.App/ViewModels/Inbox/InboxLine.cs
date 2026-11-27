namespace Capacitor.App.ViewModels.Inbox;

/// <summary>A context line: styled runs, or, when it holds only unlinked code, a column of path pills.</summary>
public sealed record InboxLine(IReadOnlyList<InboxRun> Runs, IReadOnlyList<string> Pills) {
    public bool IsEvidence => Pills.Count > 0;
}
