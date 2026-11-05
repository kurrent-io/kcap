namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>
/// The cloud sign-ins a private home would otherwise hide, so a Bedrock or Vertex user can run. These directories are
/// linked rather than named in the environment because AWS SSO's token cache has no override, and neither holds
/// OpenCode config, so the isolation stands. On Windows the link is a junction, which needs no privilege, and only
/// <c>.aws</c> needs one: Google's library reads <c>%APPDATA%</c>, which a private home does not move.
/// </summary>
internal static class OpenCodeHomeCredentials {
    static readonly string[][] Directories = OperatingSystem.IsWindows() ? [[".aws"]] : [[".aws"], [".config", "gcloud"]];

    /// <summary>A failure only costs that sign-in, which the run reports as a failed sign-in, so it does not stop the run.</summary>
    public static void Apply(string userHome, string privateHome, Action<string> log) {
        foreach (var segments in Directories) {
            var relative = Path.Combine(segments);
            var source   = Path.Combine(userHome, relative);

            if (!Directory.Exists(source)) continue;

            try {
                var link = Path.Combine(privateHome, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);

                // A junction cannot point at a network share, where a symlink is the only link there is.
                if (OperatingSystem.IsWindows() && !source.StartsWith(@"\\", StringComparison.Ordinal)) WindowsJunction.Create(link, source);
                else Directory.CreateSymbolicLink(link, source);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                log($"OpenCode's {relative} sign-in could not be linked: {ex.Message}");
            }
        }
    }
}
