using System.Reactive;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Inbox;

/// <summary>One inbox card. Holds no answer logic: whether an answer is taken is decided by the server, and the card
/// shows the sentence it returns.</summary>
public sealed class InboxItemViewModel : ReactiveObject {
    public const string ReplyAnswer = "reply";

    readonly Func<InboxItemViewModel, string, string?, Task<InboxAnswerOutcome>> _answer;

    public string                         LogicalId     { get; }
    public long                           Version       { get; }
    public string                         Type          { get; }
    public bool                           IsUnread      { get; }
    public InboxTile                      Tile          { get; }
    public IReadOnlyList<InboxRun>        Headline      { get; }
    public string?                        Quote         { get; }
    public IReadOnlyList<InboxLine>       Lines         { get; }
    public IReadOnlyList<string>          Chips         { get; }
    public string?                        StatusLine    { get; }
    public IReadOnlyList<InboxButtonViewModel> Buttons  { get; }
    public string?                        ReplyLabel    { get; }
    public string                         When          { get; }
    public string?                        LinkUrl       { get; }
    public string                         LinkText      { get; }
    public bool                           LinkExternal  { get; }

    public bool HasQuote      => !string.IsNullOrWhiteSpace(Quote);
    public bool HasChips      => Chips.Count > 0;
    public bool HasStatusLine => !string.IsNullOrWhiteSpace(StatusLine);
    public bool HasButtons    => Buttons.Count > 0;
    public bool HasReply      => ReplyLabel is not null;
    public bool HasLink       => LinkUrl is not null;
    public bool CanAnswer     => HasButtons || HasReply;

    public string? ReplyText {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsAnswering {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? Note {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ReactiveCommand<Unit, Unit> MarkReadCommand  { get; }
    public ReactiveCommand<Unit, Unit> DismissCommand   { get; }
    public ReactiveCommand<Unit, Unit> SendReplyCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenLinkCommand  { get; }
    /// <summary>Opens a link carried by one of the card's runs.</summary>
    public ReactiveCommand<string, Unit> OpenUrlCommand { get; }

    public InboxItemViewModel(NotificationItemDto dto, DateTimeOffset now, Func<string, string?> resolve,
            Func<InboxItemViewModel, Task> markRead, Func<InboxItemViewModel, Task> dismiss,
            Func<InboxItemViewModel, string, string?, Task<InboxAnswerOutcome>> answer, Action<string> open) {
        _answer      = answer;
        LogicalId    = dto.LogicalId;
        Version      = dto.Version;
        Type         = dto.Type;
        IsUnread     = dto.IsUnread;
        Tile         = dto.Type switch { "file_collision" => InboxTile.Warning, "cli_update_available" => InboxTile.Update, _ => InboxTile.Info };
        Headline     = dto.Headline is { Count: > 0 } headline ? Runs(headline, resolve) : [new InboxRun(dto.Title, InboxRunStyle.Plain, null)];
        Quote        = dto.Quote;
        Lines        = (dto.Lines ?? []).Select(l => Line(l, resolve)).ToList();
        Chips        = dto.Chips;
        StatusLine   = dto.StatusLine;
        ReplyLabel   = dto.ReplyLabel;
        When         = RelativeTime.Format(dto.UpdatedAt, now);
        LinkUrl      = dto.LinkUrl is { } url ? resolve(url) : null;
        LinkText     = string.IsNullOrWhiteSpace(dto.LinkLabel) ? "Open" : dto.LinkLabel;
        LinkExternal = dto.LinkExternal;

        var canAnswer = this.WhenAnyValue(x => x.IsAnswering, busy => !busy);
        Buttons = (dto.Buttons ?? []).Select(b => new InboxButtonViewModel(b.Answer, b.Label, b.Detail, b.Style == "primary", b.Style == "danger",
            ReactiveCommand.CreateFromTask(() => AnswerAsync(b.Answer, null), canAnswer))).ToList();
        MarkReadCommand  = ReactiveCommand.CreateFromTask(() => markRead(this));
        DismissCommand   = ReactiveCommand.CreateFromTask(() => dismiss(this));
        SendReplyCommand = ReactiveCommand.CreateFromTask(() => AnswerAsync(ReplyAnswer, ReplyText),
            this.WhenAnyValue(x => x.IsAnswering, x => x.ReplyText, (busy, text) => !busy && !string.IsNullOrWhiteSpace(text)));
        OpenLinkCommand  = ReactiveCommand.Create(() => { if (LinkUrl is { } link) open(link); });
        OpenUrlCommand   = ReactiveCommand.Create<string>(open);
    }

    /// <summary>Takes over the answer sentence, the unsent reply and an answer still in flight from the card this one
    /// replaces.</summary>
    public void Carry(string? note, string? replyText, bool answering) {
        Note ??= note;
        ReplyText ??= replyText;
        if (answering) IsAnswering = true;
    }

    /// <summary>Ends the answer this card took over from the card it replaced.</summary>
    public void AnswerSettled() => IsAnswering = false;

    async Task AnswerAsync(string answer, string? text) {
        if (IsAnswering) return;
        IsAnswering = true;
        try {
            var outcome = await _answer(this, answer, text);
            Note = outcome.Note;
            // A refused or unsent reply keeps its draft, so the person can try again without retyping it.
            if (text is not null && outcome.Accepted) ReplyText = null;
        } finally {
            IsAnswering = false;
        }
    }

    static IReadOnlyList<InboxRun> Runs(IEnumerable<NotificationRunDto> runs, Func<string, string?> resolve) =>
        runs.Select(r => new InboxRun(r.Text, StyleOf(r.Style), r.Url is { } url ? resolve(url) : null)).ToList();

    // The evidence-list shape: only unlinked code runs and whitespace gaps, shown as a column so paths stay readable.
    static InboxLine Line(IReadOnlyList<NotificationRunDto> line, Func<string, string?> resolve) {
        var evidence = line.Any(r => r.Style == "code" && r.Url is null)
                    && line.All(r => r.Url is null && (r.Style == "code" || (r.Style == "plain" && string.IsNullOrWhiteSpace(r.Text))));
        return evidence
            ? new InboxLine([], line.Where(r => r.Style == "code").Select(r => r.Text).ToList())
            : new InboxLine(Runs(line, resolve), []);
    }

    static InboxRunStyle StyleOf(string style) => style switch {
        "strong" => InboxRunStyle.Strong,
        "code"   => InboxRunStyle.Code,
        _        => InboxRunStyle.Plain,
    };
}
