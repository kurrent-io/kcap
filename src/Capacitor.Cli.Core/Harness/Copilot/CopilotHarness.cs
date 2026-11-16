using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>Copilot as this process sees it.</summary>
public sealed class CopilotHarness : IHarness<CopilotHarness> {
    CopilotHarness(CopilotPaths paths) => Paths = paths;

    /// <summary>Resolves <c>COPILOT_HOME</c>, which replaces the whole <c>~/.copilot</c> path, and
    /// <c>XDG_CONFIG_HOME</c>, which moves VS Code's user data on Linux.</summary>
    public static CopilotHarness FromEnvironment(UserHome home) => Over(new(
        home,
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        Environment.GetEnvironmentVariable("COPILOT_HOME")));

    /// <summary>Over a layout resolved elsewhere — a reviewer's isolated home, or a test's.</summary>
    public static CopilotHarness Over(CopilotPaths paths) => new(paths);

    public static HarnessId Id        => HarnessId.Copilot;
    public static string    Label     => "Copilot";
    public static string    CliBinary => "copilot";

    /// <summary>This vendor's layout. Public because our own readers of its files take the typed
    /// paths; they reach them through the instance the entry point built, never by resolving the
    /// override a second time.</summary>
    public CopilotPaths Paths { get; }

    public IReadOnlyList<string> IntegrationFiles => [Paths.KcapHooksJson, Paths.McpConfigJson];

    const string VsCode = "VS Code";

    static readonly string[] VsCodeExtensionData = [
        Path.Combine("Code", "User", "globalStorage", "github.copilot-chat"),
        Path.Combine("Code - Insiders", "User", "globalStorage", "github.copilot-chat"),
    ];

    // VS Code's own copilot shim sits on the user's PATH with no Copilot CLI behind it.
    static bool IsVsCodeShim(string path) =>
        path.Split('/', '\\').Any(segment => segment.Equals("copilot-shim", StringComparison.OrdinalIgnoreCase));

    bool InVsCode(DesktopAppProbe apps) => Paths.HasVsCodeSignIn() || apps.Finds(VsCodeExtensionData);

    // The marker covers users who launch Copilot through an IDE wrapper; the PATH probe covers
    // a fresh install that has not created ~/.copilot yet.
    public HarnessSignals Signals => new() {
        LaunchSignal   = probe => probe.Finds(CliBinary),
        UserDataSignal = Paths.HasUserData,
        AppSignal      = InVsCode,
        HostApp        = (binaries, apps) => binaries.Resolve(CliBinary) is { } cli
            ? IsVsCodeShim(cli) ? VsCode : null
            : InVsCode(apps) ? VsCode : null,
        Wired          = () => CopilotHooksInstaller.IsInstalled(Paths.KcapHooksJson),
    };
}
