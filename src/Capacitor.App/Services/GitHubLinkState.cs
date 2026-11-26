namespace Capacitor.App.Services;

/// Where a GitHub pull request or issue stands. Draft and Merged only ever describe a pull request.
public enum GitHubLinkState {
    Open,
    Draft,
    Merged,
    Closed,
}
