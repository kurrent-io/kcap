using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// A pull-request or issue card. The URL is server-returned, so it crosses the same trust boundary
/// the chat tab applies before a link reaches the shell opener.
public sealed class WorkContextLinkViewModel {
    public const string TitleMissingNote = "Title not available";

    public string  Eyebrow  { get; }
    public string  Key      { get; }
    public string  Title    { get; }
    public string? Url      { get; }
    /// Server kind/provider/value when projected from a work-item link; empty for legacy PR cards.
    public string  Identity { get; }
    public bool    CanOpen  { get; }
    public string  AccessibleName => $"{Key}: {(Title.Length == 0 ? TitleMissingNote : Title)}";
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    public WorkContextLinkViewModel(string eyebrow, string key, string title, string? url, IUrlOpener opener, string identity = "") {
        Eyebrow  = eyebrow;
        Key      = key;
        Title    = title;
        Url      = url;
        Identity = identity;
        CanOpen  = LinkPolicy.IsOpenable(url);
        OpenCommand = ReactiveCommand.Create(() => LinkPolicy.Open(opener, url), Observable.Return(CanOpen));
    }
}

/// One person with a session attached to the work item. The pane draws <see cref="Initial"/> beneath
/// <see cref="AvatarUrl"/>, so a missing or failed picture shows the letter.
public sealed class WorkContextPersonViewModel {
    public string  Name             { get; }
    public string  Initial          { get; }
    public string? AvatarUrl        { get; }
    public string  LastActivityText { get; }
    public string  UserId           { get; }

    public WorkContextPersonViewModel(string name, string? avatarUrl, DateTimeOffset? lastActivityAt, DateTimeOffset now, string userId = "") {
        Name             = name;
        Initial          = InitialOf(name);
        AvatarUrl        = avatarUrl;
        LastActivityText = lastActivityAt is { } at ? RelativeTime.Format(at, now) : "";
        UserId           = userId;
    }

    /// The first text element, so a surrogate pair or a combining sequence stays whole.
    public static string InitialOf(string name) => new StringInfo(name).SubstringByTextElements(0, 1).ToUpperInvariant();
}
