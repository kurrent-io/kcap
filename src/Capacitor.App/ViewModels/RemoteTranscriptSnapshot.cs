using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.App.ViewModels;

/// What a closed remote transcript leaves behind: its projected lines in stream order, the last event number
/// applied, how many stream events lie before the oldest loaded one and that event's number, the event number
/// `from` counted from, and the prompts the loaded events scheduled, which the next events are read under.
internal sealed record RemoteTranscriptSnapshot(
    IReadOnlyList<ProjectedLine> Lines, long Position, long Earliest, long? Oldest, long Base, ScheduledPrompts Scheduled);
