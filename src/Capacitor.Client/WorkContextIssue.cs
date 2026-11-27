namespace Capacitor.Client;

/// `Key` is display text only, which issues from two repositories can share; `Identity` is the
/// server's kind/provider/value. `State` is the server's word for the issue's lifecycle.
public sealed record WorkContextIssue(string Key, string Title, string? Url, string? State, string Identity);
