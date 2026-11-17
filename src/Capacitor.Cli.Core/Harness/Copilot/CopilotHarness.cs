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
    // A link to it, or to its folder, is it too.
    static bool IsVsCodeShim(string path) => InShimFolder(path) || InShimFolder(WithLinksResolved(path));

    static bool InShimFolder(string path) =>
        path.Split('/', '\\').Any(segment => segment.Equals("copilot-shim", StringComparison.OrdinalIgnoreCase));

    // Component by component, because a link can stand for the file or for any folder above it.
    static string WithLinksResolved(string path) {
        var full     = Path.GetFullPath(path);
        var root     = Path.GetPathRoot(full) ?? "";
        var resolved = root;

        foreach (var part in full[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)) {
            resolved = Path.Combine(resolved, part);
            if (LinkTarget(resolved) is { } target) resolved = target;
        }

        return resolved;
    }

    static string? LinkTarget(string path) {
        try {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>The Copilot CLI <paramref name="command"/> names on the search path, passing over VS Code's
    /// launcher: it only runs a CLI installed elsewhere, and with none it prints that Copilot CLI was not found.</summary>
    public static string? ResolveCli(BinaryProbe binaries, string? command) => binaries.Resolve(command, IsVsCodeShim);

    bool InVsCode(DesktopAppProbe apps) => Paths.HasVsCodeSignIn() || apps.Finds(VsCodeExtensionData);

    // The marker covers users who launch Copilot through an IDE wrapper; the PATH probe covers
    // a fresh install that has not created ~/.copilot yet.
    public HarnessSignals Signals => new() {
        LaunchSignal   = probe => probe.Finds(CliBinary),
        UserDataSignal = Paths.HasUserData,
        AppSignal      = InVsCode,
        HostApp        = (binaries, apps) => ResolveCli(binaries, CliBinary) is null && (binaries.Finds(CliBinary) || InVsCode(apps))
            ? VsCode
            : null,
        Wired          = () => CopilotHooksInstaller.IsInstalled(Paths.KcapHooksJson),
    };
}
