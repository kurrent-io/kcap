namespace Capacitor.Cli.Core.Config;

public static class ExperimentalFeatures {
    public static ExperimentalFeature CodexAppServer { get; } = new(
        "codex-app-server",
        "Codex app-server",
        "Run Codex reviewers and interactive Codex agents over codex app-server instead of the terminal. Needs Codex 0.146.0 or later.",
        AppliesLive: true,
        OnByDefault: OperatingSystem.IsWindows());

    public static ExperimentalFeature InAppPages { get; } = new(
        "in-app-pages",
        "In-app pages",
        "Open kcap Pages inside the desktop app instead of the browser, where interactive pages can be answered. macOS, and Windows with the WebView2 runtime.",
        AppliesLive: true,
        DesktopAppOnly: true,
        OnByDefault: false);

    public static ExperimentalFeature LauncherNextWork { get; } = new(
        "launcher-next-work",
        "What to do next in the launcher",
        "Show the server's next-work suggestions under the desktop launcher, as the web dashboard does, and start a session from one. Needs a server with next work turned on.",
        AppliesLive: true,
        DesktopAppOnly: true,
        OnByDefault: false);

    public static IReadOnlyList<ExperimentalFeature> All { get; } = [CodexAppServer, InAppPages, LauncherNextWork];

    /// <summary>Ids are matched exactly, case included.</summary>
    public static ExperimentalFeature? Find(string id) =>
        All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.Ordinal));
}
