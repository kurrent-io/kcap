namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>One read of a chat. <see cref="FromThreadStart"/> is false when the source could not reach the
/// chat's first entry, so the oldest entry it returned may sit in the middle of a session.</summary>
public sealed record GrokBotRead(IReadOnlyList<GrokBotEntry> Entries, bool FromThreadStart) {
    public static readonly GrokBotRead Nothing = new([], true);
}
