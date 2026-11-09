namespace Capacitor.App.ViewModels.NextWork;

public sealed record NextWorkFacets(int OthersWaiting, int? TrackerPriority, bool IsBug, int SightingCount, double StalenessDays) {
    public static readonly NextWorkFacets None = new(0, null, false, 0, 0);
}
