using System.Reactive;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.NextWork;

/// <summary>One row of the launcher's quick start: glyph, title and one line of facts. Pressing it fills the composer;
/// ▶ starts an agent on it at once; its menu holds the rest.</summary>
public sealed class NextWorkCardViewModel : ReactiveObject {
    readonly NextWorkBandViewModel _band;

    public NextWorkCardViewModel(NextWorkBandViewModel band, NextWorkFocusItem item, bool isNew,
            IReadOnlyDictionary<string, string> repoNames, bool canLaunch) {
        _band = band;
        var row = item.Row;
        Row        = row;
        IsNew      = isNew;
        Label      = NextWorkLaneLogic.DisplayLabel(row);
        Glyph      = NextWorkGlyphViewModel.From(NextWorkLaneLogic.Glyph(row));
        Href       = row.TargetHref;
        LooseEndId = NextWorkLaneLogic.LooseEndIdOf(row);
        ArmChip    = NextWorkLaneLogic.ArmChip(row);
        ArmChipWarns = NextWorkLaneLogic.GroupOf(row) == NextWorkGroup.Waiting;
        Marks      = [.. NextWorkLaneLogic.RowMarks(row).Select(m => new NextWorkChip(m, NextWorkLaneLogic.MarkTone(m)))];
        CanWorkOn  = canLaunch && NextWorkLaneLogic.IsLaunchable(row) && row.RepoHash is not null;

        var ownEnd = LooseEndId is not null;
        // A loose end's reason is its own text, the title already; its line says when it was noted instead.
        // Approved-unmerged's because restates the arm ("Approved, unmerged…"); KindLabel already says Ready to merge.
        var because = ownEnd && NextWorkLaneLogic.BecauseIsLabel(row) ? null
            : row.Arm == "ApprovedUnmerged" ? null
            : NextWorkLaneLogic.WithoutAge(NextWorkLaneLogic.Reason(row));
        var age     = ownEnd ? NextWorkLaneLogic.NotedAgo(row) : NextWorkLaneLogic.Age(row);
        KindLabel = NextWorkLaneLogic.KindLabel(row);
        KindTip   = NextWorkLaneLogic.KindTip(row);

        // The loose end an item carries is why it is worth picking up now; a loose end's own row is its title.
        // What the server's prompt adds, said for a person: the prompt itself is written for the agent.
        ContextSummary = ownEnd
            ? "The agent also gets the session this loose end was noted in."
            : item.Ends.Count switch {
                0 => null,
                1 => "The agent also gets the loose end noted against it and the session it came from.",
                var n => $"The agent also gets the {n} loose ends noted against it and the sessions they came from.",
            };

        if (!ownEnd && item.Ends.Count > 0) {
            var first = item.Ends[0].Text;
            Context = item.Ends.Count > 1 ? $"{first} · +{item.Ends.Count - 1} more" : first;
            // Because often is that same loose-end text; saying it twice fills the row with two truncated copies.
            if (because is not null && string.Equals(because.Trim(), first.Trim(), StringComparison.OrdinalIgnoreCase))
                because = null;
        }

        Meta = string.Join(" · ", new[] { because, NextWorkLaneLogic.ShortRepo(repoNames, row.RepoHash), age }.Where(p => p is { Length: > 0 }));

        FillCommand    = ReactiveCommand.CreateFromTask(() => _band.FillAsync(this));
        StartCommand   = ReactiveCommand.CreateFromTask(() => _band.StartNowAsync(this));
        OpenCommand    = ReactiveCommand.Create(() => _band.Open(this));
        DismissCommand = ReactiveCommand.Create(() => _band.Dismiss(this));
        DoneCommand    = ReactiveCommand.Create(() => { if (LooseEndId is { } id) _band.CloseLooseEnd(id); });
        SnoozeCommand  = ReactiveCommand.Create(() => { if (LooseEndId is { } id) _band.Snooze(id); });
    }

    public NextWorkRow                 Row          { get; }
    public string                      Key          => Row.TargetKey;
    public string                      Label        { get; }
    public NextWorkGlyphViewModel      Glyph        { get; }
    public string?                     Href         { get; }
    public string?                     LooseEndId   { get; }
    public bool                        IsNew        { get; }
    public string?                     ArmChip      { get; }
    public bool                        ArmChipWarns { get; }
    public IReadOnlyList<NextWorkChip> Marks        { get; }
    public bool                        CanWorkOn    { get; }

    /// <summary>What the row is, leading the line under the title; the icon alone does not say.</summary>
    public string KindLabel { get; }

    /// <summary>The reason, repository and age, after the kind and any marks.</summary>
    public string Meta { get; }

    /// <summary>The icon's tooltip: what the kind is.</summary>
    public string KindTip { get; }

    /// <summary>The first loose end noted against the item, and how many more there are; null for a row without one.</summary>
    public string? Context { get; }

    /// <summary>What the agent gets beyond the task's line, for the composer chip's tooltip; null when nothing.</summary>
    public string? ContextSummary { get; }

    public bool HasMeta          => Meta.Length > 0;
    public bool HasContext       => Context is not null;
    public bool HasArmChip       => ArmChip is not null;
    public bool HasMarks         => Marks.Count > 0;
    public bool IsLooseEnd       => LooseEndId is not null;
    public bool CanOpenInBrowser => Href is not null;

    /// <summary>The menu holds something above Dismiss, which a separator then sets apart.</summary>
    public bool HasMenuActions   => CanOpenInBrowser || IsLooseEnd;

    public string FillTip => Row.TargetKind == "session" ? "Open this session" : "Put this in the composer";

    /// <summary>Row name for assistive tech; prefixes New when the visit-dot is showing.</summary>
    public string AccessibleName => IsNew ? $"New: {Label}" : Label;

    /// <summary>While its launch options are being read, for the fill or a start.</summary>
    public bool Busy {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>This row's goal is the one in the composer.</summary>
    public bool IsFilled {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit>   FillCommand    { get; }
    public ReactiveCommand<Unit, Unit>   StartCommand   { get; }
    public ReactiveCommand<Unit, Unit>   OpenCommand    { get; }
    public ReactiveCommand<Unit, Unit>   DismissCommand { get; }
    public ReactiveCommand<Unit, Unit>   DoneCommand    { get; }
    public ReactiveCommand<Unit, Unit>   SnoozeCommand  { get; }
}
