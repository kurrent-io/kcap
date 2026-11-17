namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>
/// The oldest OpenCode kcap's plugin supports. The plugin's single file exports the v1 entry point and
/// v2's <c>Plugin.define</c> form together, which OpenCode v1 documents as supported only from this
/// release.
/// </summary>
public static class OpenCodeVersionFloor {
    public const string Minimum = "1.18.29";

    /// <summary>What to tell the user when <paramref name="installed"/> is below the floor; null when it
    /// is supported or cannot be read, so an unknown build is never warned about.</summary>
    public static string? UpgradeNotice(string? installed) {
        if (string.IsNullOrWhiteSpace(installed) || !Version.TryParse(installed, out var version)) return null;

        return version < Version.Parse(Minimum)
            ? $"OpenCode {installed} is older than {Minimum}, the oldest version kcap's plugin supports. "
            + "Run `opencode upgrade`, then restart OpenCode."
            : null;
    }
}
