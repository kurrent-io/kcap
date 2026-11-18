using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// The <c>kcap-steering</c> Claude Code plugin, which goes wherever kcap's own plugin is installed: the default
/// settings file and each Claude config dir <c>kcap accounts</c> records, once per file. Where kcap's plugin isn't
/// installed it goes nowhere.
/// </summary>
/// <param name="Targets">Each settings file, with the config dir a hand install has to name, null for the default.</param>
internal sealed record ClaudeSteeringMod(IReadOnlyList<(string SettingsPath, string? ConfigDir)> Targets) {
    public static ClaudeSteeringMod For(HarnessRegistry harnesses, UserHome home, AccountStore accounts) {
        List<(string, string?)> targets = [(harnesses.Of<ClaudeHarness>().Paths.UserSettings, null)];
        var defaultDir = AccountLayouts.DefaultDirectory(HarnessId.Claude, home);

        foreach (var account in (accounts.TryLoad()?.Accounts ?? []).Where(account => account.Vendor == HarnessId.Claude)) {
            var settings = AccountLayouts.Claude(home, account.Directory).UserSettings;
            if (targets.Any(t => t.Item1 == settings)) continue;

            targets.Add((settings, AccountDirectory.Same(account.Directory, defaultDir) ? null : account.Directory));
        }

        return new(targets);
    }

    /// <summary>Whether kcap's plugin is installed in any of the targets, so that steering can reach a session.</summary>
    public bool KcapPluginInstalled() => Targets.Any(t => ClaudePluginInstaller.HasKcapMarketplace(t.SettingsPath));

    public ClaudeSteeringModInstall InstallOnce() {
        List<string> notes = [], failures = [];
        var withoutKcap = 0;

        foreach (var (settings, configDir) in Targets) {
            var claude = configDir is null ? "claude plugin" : $"CLAUDE_CONFIG_DIR={configDir} claude plugin";
            var edit   = ClaudePluginInstaller.HasInstalledSteering(settings) ? SettingsEdit.Unchanged : ClaudePluginWriter.InstallSteering(settings);

            switch (edit) {
                case null:
                    withoutKcap++;

                    continue;
                case SettingsEdit.Malformed:
                    failures.Add(Failed(settings, $"kcap couldn't read {settings} as settings, so it left the file alone", claude));

                    continue;
                case SettingsEdit.Failed:
                    failures.Add(Failed(settings, $"{settings} couldn't be written", claude));

                    continue;
                case SettingsEdit.Changed:
                    notes.Add($"Installed the kcap-steering plugin for Claude Code in {settings}; it loads in the sessions you start from now on.");

                    break;
            }

            if (ClaudePluginInstaller.IsSteeringEnabled(settings) is not true)
                notes.Add($"kcap-steering isn't enabled in {settings}, so the Claude Code sessions that use it won't be steered; "
                        + $"`{claude} enable kcap-steering@kcap` turns it back on, or `{claude} install kcap-steering@kcap` if it was removed.");
        }

        if (withoutKcap == Targets.Count)
            failures.Add("the kcap-steering plugin isn't installed: steering a Claude Code session needs kcap's Claude Code plugin, "
                       + $"which isn't installed in {Targets[0].SettingsPath}{(Targets.Count > 1 ? " or any Claude config dir kcap accounts records" : "")}. "
                       + "`kcap plugin install` adds it, and then `kcap daemon steering on` installs the steering plugin.");

        return new(notes, failures);
    }

    static string Failed(string settings, string why, string claude) =>
        $"the kcap-steering plugin isn't installed in {settings}: {why}. Install it by hand with `{claude} install kcap-steering@kcap`, "
      + "or run `kcap daemon steering on` again once that's fixed.";
}
