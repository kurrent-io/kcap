namespace Capacitor.Models.Transcripts.Harness.Muse;

/// Usage a Muse run reported after its last assistant event, so no event of the run could carry it. Persisted as
/// `MuseUsageBackfilled` with an empty payload: the tokens ride the event's usage, read as on an assistant event.
public sealed record MuseUsageBackfill;
