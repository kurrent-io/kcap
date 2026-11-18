using System.Globalization;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The first message a resumed session receives. It is a new turn, never a re-send of the
/// interrupted one, so it carries no prompt text: a re-sent instruction can repeat a side effect.
/// </summary>
internal static class ResumeNudge {
    /// <param name="zone">The machine's own zone when null, the one the chat shows its times in.</param>
    public static string Build(DateTimeOffset interruptedAt, string? originalPrompt, TimeZoneInfo? zone = null) {
        var at   = TimeZoneInfo.ConvertTime(interruptedAt, zone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
        var task = string.IsNullOrWhiteSpace(originalPrompt) ? "where you left off" : "the task you were given";

        return $"This session was interrupted by a kcap daemon restart at {at}. "
             + "The previous turn may have stopped mid-way, so a command may not have finished and its result may be missing. "
             + "Check the working tree and your last steps before continuing, and do not repeat an action you can verify already happened. "
             + $"Then continue with {task}.";
    }
}
