namespace Capacitor.App.ViewModels.NextWork;

/// <summary>What a band row puts in the launcher's composer: a short goal to read and edit, the machine and checkout to
/// run it in when one is known, and <see cref="Context"/>, the prompt the server built for the item, which the launch
/// adds under the goal. <see cref="Label"/> names the row on the composer's source chip.</summary>
public sealed record NextWorkFill(string Key, string Label, string Goal, string? Machine, bool MachineIsLocal, string? RepoPath,
    string? Context = null);
