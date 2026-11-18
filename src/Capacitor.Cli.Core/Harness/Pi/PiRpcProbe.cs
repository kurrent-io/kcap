using System.Diagnostics;
using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Pi;

public static class PiRpcProbe {
    // --offline stops Pi's startup network work (a configured package install runs even with --no-extensions);
    // --no-extensions keeps third-party extension code from running for a question.
    public static ProcessStartInfo StartInfo(string pi, string workingDirectory) {
        var psi = new ProcessStartInfo {
            FileName               = pi,
            WorkingDirectory       = workingDirectory,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var arg in new[] { "--mode", "rpc", "--offline", "--no-extensions", "--no-session" })
            psi.ArgumentList.Add(arg);

        return psi;
    }

    public static bool Answers(JsonElement frame, string command) =>
        frame.IsObject && frame.Str("type") == "response" && frame.Str("command") == command;
}
