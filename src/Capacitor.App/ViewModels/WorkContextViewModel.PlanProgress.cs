using System.ComponentModel;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed partial class WorkContextViewModel {
    public bool ShowsPlanMergeNote => Plan.HasTasks && Plan.DoneCount < Plan.Tasks.Count
        && PullRequests is { MergedPullRequests.Length: > 0 };

    public string PlanMergeNote {
        get {
            if (!ShowsPlanMergeNote) return "";
            var remaining = Plan.Tasks.Count - Plan.DoneCount;
            var tasks = remaining == 1 ? "1 task" : $"{remaining} tasks";
            return $"{PullRequests!.MergedPullRequests} merged with {tasks} still open. Merges don’t update the plan.";
        }
    }

    void OnPlanProgressChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName is nameof(PlanSectionViewModel.HeaderText)) RaisePlanProgress();
    }

    void RaisePlanProgress() {
        this.RaisePropertyChanged(nameof(ShowsPlanMergeNote));
        this.RaisePropertyChanged(nameof(PlanMergeNote));
    }
}
