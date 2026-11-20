using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli;

/// <summary>The desktop app names the server it shows in <c>KCAP_EXPECT_SERVER_URL</c>; a profile repointed since
/// must not carry a session change to another server, where the same id may name someone else's session.</summary>
static class ExpectedServer {
    public const string Var = "KCAP_EXPECT_SERVER_URL";

    /// <summary>Why the command must not run, or null when nothing is expected or the servers agree.</summary>
    public static string? Mismatch(Func<string, string?> getEnv, string resolvedUrl) =>
        getEnv(Var) is { Length: > 0 } expected && !ServerIdentity.SameServer(expected, resolvedUrl)
            ? $"kcap is pointed at {(resolvedUrl.Length > 0 ? resolvedUrl : "no server")}, not {expected}. Nothing was changed."
            : null;
}
