using Capacitor.App.Services.Driver;

namespace Capacitor.App.ViewModels.Driver;

/// <summary>One work item in flight: its row on the overview and its page.</summary>
public sealed class DriverWorkItemViewModel {
    public DriverWorkItemViewModel(DriverWorkItemsDto.WorkItem item, IReadOnlyList<DriverPartViewModel> parts, DriverActivity ownActivity,
            int needs) {
        WorkItemId     = item.WorkItemId;
        Title          = item.Title;
        Goal           = item.Goal;
        Workspace      = item.Workspace?.Name;
        Status         = item.Status;
        LastActivityAt = item.LastActivityAt;
        Parts          = parts.OrderBy(p => p.Order).ToList();
        Activity       = parts.Select(p => p.Activity).Append(ownActivity).Max();
        Needs          = needs;
        Working        = parts.Count(p => p.IsWorking) + (ownActivity == DriverActivity.Working ? 1 : 0);
    }

    public string                             WorkItemId     { get; }
    public string                             Title          { get; }
    public string?                            Goal           { get; }
    public string?                            Workspace      { get; }
    public string                             Status         { get; }
    public DateTimeOffset?                    LastActivityAt { get; }
    public IReadOnlyList<DriverPartViewModel> Parts          { get; }
    public DriverActivity                     Activity       { get; }
    public int                                Needs          { get; }
    public int                                Working        { get; }

    public bool   HasGoal      => !string.IsNullOrWhiteSpace(Goal);
    public bool   HasWorkspace => Workspace is not null;
    public bool   HasParts     => Parts.Count > 0;
    public bool   HasNeeds     => Needs > 0;
    public bool   IsWorking    => Activity == DriverActivity.Working;
    public string StatusLabel  => DriverActivities.StatusLabel(Status);
    public string NeedsText    => Needs == 1 ? "1 needs you" : $"{Needs} need you";
    public string WorkingText  => Working switch { 0 => "", 1 => "1 agent working", _ => $"{Working} agents working" };

    public string PartsSummary {
        get {
            if (Parts.Count == 0) return "No parts";
            var done = Parts.Count(p => p.IsDone);
            return $"{done} of {Parts.Count} parts done";
        }
    }
}
