using Capacitor.Cli.Commands.Harness;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// The synchronous Claude hooks that run beside the main one, each named by a flag. Several flags on one invocation
/// run their handlers concurrently in one process, so the manifest can give an event one process where it would
/// otherwise start one per handler; Claude runs an event's hooks in parallel, so running them one after another
/// here would make the event slower.
/// </summary>
static class ClaudeSideHooks {
    public const string PlanRead  = "--plan-read";
    public const string TaskList  = "--task-list";
    public const string PlanEdit  = "--plan-edit";

    /// <summary>On a Stop, beside the main hook: closes the turn's workspace spans while the Stop is posted. A binary
    /// that predates the flag ignores it and still posts the Stop, which is the part that must not be lost.</summary>
    public const string StopSpans = "--stop-spans";

    static readonly string[] Flags = [PlanRead, TaskList, PlanEdit, ClaudeWorkspaceSpanEvent.Flag, ClaudePassNotices.Flag];

    /// <summary>Whether the side hook can write to stdout, which then has to be merged with the others'.</summary>
    public static bool Writes(string flag) => flag is PlanRead or TaskList or ClaudePassNotices.Flag;

    public static IReadOnlyList<string> Requested(IReadOnlyCollection<string> args) => [.. Flags.Where(args.Contains)];

    public static async Task<int> RunAsync(string flag, ClaudeHookCommand command, string body, TextWriter stdout) => flag switch {
        PlanRead                       => await command.HandlePlanRead(new StringReader(body), stdout),
        TaskList                       => await command.HandleTaskList(new StringReader(body), stdout),
        PlanEdit                       => await command.HandlePlanEdit(new StringReader(body)),
        ClaudeWorkspaceSpanEvent.Flag  => await command.HandleWorkspaceSpan(new StringReader(body)),
        ClaudePassNotices.Flag         => await command.HandlePassNotices(new StringReader(body), stdout),
        _                              => throw new ArgumentOutOfRangeException(nameof(flag), flag, "not a side hook"),
    };
}
