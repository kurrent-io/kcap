namespace Capacitor.App.ViewModels.NextWork;

/// <summary>What a band row puts in the launcher's composer: the goal, and the machine and checkout to run it in when
/// one is known. <see cref="Label"/> names the row on the composer's source chip.</summary>
public sealed record NextWorkFill(string Key, string Label, string Goal, string? Machine, bool MachineIsLocal, string? RepoPath);
