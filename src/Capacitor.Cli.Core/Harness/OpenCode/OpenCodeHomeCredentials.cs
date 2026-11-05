namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>
/// The cloud sign-ins a private home would otherwise hide, so a Bedrock or Vertex user can run. These directories are
/// linked rather than named in the environment because AWS SSO's token cache has no override, and neither holds
/// OpenCode config, so the isolation stands. A symlink needs a privilege on Windows, where Google's library reads
/// <c>%APPDATA%</c>, which a private home does not move, and only the AWS files need naming.
/// </summary>
internal static class OpenCodeHomeCredentials {
    static readonly string[][] Directories = [[".aws"], [".config", "gcloud"]];

    /// <summary>A failure only costs that sign-in, which the run reports as a failed sign-in, so it does not stop the run.</summary>
    public static void Apply(IDictionary<string, string?> environment, string userHome, string privateHome, Action<string> log) {
        if (OperatingSystem.IsWindows()) {
            Name(environment, "AWS_SHARED_CREDENTIALS_FILE", Path.Combine(userHome, ".aws", "credentials"));
            Name(environment, "AWS_CONFIG_FILE", Path.Combine(userHome, ".aws", "config"));

            return;
        }

        foreach (var segments in Directories) {
            var relative = Path.Combine(segments);
            var source   = Path.Combine(userHome, relative);

            if (!Directory.Exists(source)) continue;

            try {
                var link = Path.Combine(privateHome, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                Directory.CreateSymbolicLink(link, source);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                log($"OpenCode's {relative} sign-in could not be linked: {ex.Message}");
            }
        }
    }

    static void Name(IDictionary<string, string?> environment, string variable, string file) {
        if (environment.TryGetValue(variable, out var set) && !string.IsNullOrEmpty(set)) return;
        if (File.Exists(file)) environment[variable] = file;
    }
}
