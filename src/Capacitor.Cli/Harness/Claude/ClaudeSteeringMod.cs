using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>The <c>kcap-steering</c> Claude Code plugin, installed into the user settings file the kcap plugin
/// uses the first time steering is turned on there, and never again.</summary>
internal sealed record ClaudeSteeringMod(string SettingsPath, Func<string?> PluginDirectory) {
    public static ClaudeSteeringMod For(HarnessRegistry harnesses) =>
        new(harnesses.Of<ClaudeHarness>().Paths.UserSettings, () => SetupCommand.ResolvePluginPath());

    public ClaudeSteeringModInstall InstallOnce() {
        if (ClaudePluginInstaller.HasInstalledSteering(SettingsPath)) return default;

        if (PluginDirectory() is not { } directory) return Failed("kcap's plugin directory wasn't found");

        return ClaudePluginWriter.InstallSteering(SettingsPath, directory) switch {
            SettingsEdit.Changed   => new($"Installed the kcap-steering plugin for Claude Code in {SettingsPath}; it loads in the sessions you start from now on.", null),
            SettingsEdit.Unchanged => default,
            SettingsEdit.Malformed => Failed($"kcap couldn't read {SettingsPath} as settings, so it left the file alone"),
            _                      => Failed($"{SettingsPath} couldn't be written"),
        };
    }

    static ClaudeSteeringModInstall Failed(string why) =>
        new(null, $"the kcap-steering plugin isn't installed: {why}. Install it by hand with `claude plugin install kcap-steering@kcap`, "
                + "or run `kcap daemon steering on` again once that's fixed.");
}
