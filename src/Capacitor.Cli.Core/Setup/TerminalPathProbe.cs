namespace Capacitor.Cli.Core.Setup;

public static class TerminalPathProbe {
    /// The probe for this OS: Windows has no login shell to ask, and a GUI launch there does not
    /// start from a stripped PATH the way a launchd one does.
    /// <param name="directStart">The caller's hooks start kcap without a shell, which on Windows
    /// only a `kcap.exe` satisfies. A shell resolves the same `kcap` either way elsewhere.</param>
    public static ILoginShellProbe Create(IProcessRunner runner, Func<string, string?> getEnv, bool directStart = false) =>
        !OperatingSystem.IsWindows() ? new LoginShellProbe(runner, getEnv)
        : directStart                ? WindowsPathProbe.ForDirectStart()
        :                              new WindowsPathProbe();
}
