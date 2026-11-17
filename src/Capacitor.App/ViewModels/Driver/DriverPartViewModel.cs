using System.Reactive;
using ReactiveUI.Reactive;
using Capacitor.App.Services.Driver;

namespace Capacitor.App.ViewModels.Driver;

/// <summary>One part as the Driver's work item page shows it: its status from the server, its activity from the live sessions.</summary>
public sealed class DriverPartViewModel {
    public DriverPartViewModel(DriverWorkItemsDto.Part part, IReadOnlyDictionary<string, string> titles, DriverActivity activity,
            string? liveAgentId, Action<string>? openAgent) {
        WorkItemId = part.WorkItemId;
        Title      = part.Title;
        Status     = part.Status;
        SetByHand  = part.SetByHand;
        Activity   = activity;
        WaitsOn    = part.WaitsOn.Select(id => titles.GetValueOrDefault(id) ?? id).ToList();
        Links      = string.Join("  ", part.Links.Select(l => l.ShortKey));
        Sessions   = part.Sessions.Count;
        SessionIds = part.Sessions.Select(s => s.SessionId).ToList();
        LiveAgentId = liveAgentId;
        OpenCommand = ReactiveCommand.Create(() => { if (liveAgentId is not null) openAgent?.Invoke(liveAgentId); });
    }

    public string                WorkItemId  { get; }
    public string                Title       { get; }
    public string                Status      { get; }
    public bool                  SetByHand   { get; }
    public DriverActivity        Activity    { get; }
    public IReadOnlyList<string> WaitsOn     { get; }
    public string                Links       { get; }
    public int                   Sessions    { get; }
    public IReadOnlyList<string> SessionIds  { get; }
    public string?               LiveAgentId { get; }

    public string StatusLabel   => DriverActivities.StatusLabel(Status) + (SetByHand ? " (set by hand)" : "");
    public string ActivityLabel => DriverActivities.Label(Activity);
    public string WaitsOnText   => WaitsOn.Count == 0 ? "" : "after " + string.Join(", ", WaitsOn);
    public string SessionsText  => Sessions switch { 0 => "no sessions", 1 => "1 session", _ => $"{Sessions} sessions" };

    public bool IsDone       => Status == "done";
    public bool IsInReview   => Status == "in_review";
    public bool IsInProgress => Status == "in_progress";
    public bool IsBlocked    => Status == "blocked";
    public bool IsOpen       => !(IsDone || IsInReview || IsInProgress || IsBlocked);
    public bool IsWorking    => Activity == DriverActivity.Working;
    public bool IsWaiting    => Activity == DriverActivity.WaitingOnYou;
    public bool HasActivity  => Activity != DriverActivity.None;
    public bool CanOpen      => LiveAgentId is not null;

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    /// <summary>Review first, then running work, then what is stuck, then what has not started, then what is done.</summary>
    public int Order => Status switch {
        "in_review"   => 0,
        "in_progress" => 1,
        "blocked"     => 2,
        "done"        => 4,
        _             => 3
    };
}
