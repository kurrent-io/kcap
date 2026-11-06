namespace Capacitor.App.ViewModels.NextWork;

/// <summary>A next-work task picked for the next launch: <see cref="Goal"/>, the line the launch opens with (the item's
/// title, or what to do with a pull request); the machine and checkout to run it in when one is known; and
/// <see cref="Context"/>, the prompt the server built for the item, sent last. <see cref="Label"/> names it on the
/// composer's chip.</summary>
public sealed record NextWorkFill(string Key, string Label, string Goal, string? Machine, bool MachineIsLocal, string? RepoPath,
    string? Context = null);
