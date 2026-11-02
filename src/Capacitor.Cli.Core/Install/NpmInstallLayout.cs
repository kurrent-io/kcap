namespace Capacitor.Cli.Core.Install;

/// <summary>The layout <c>npm install -g @kurrent/kcap</c> lays down.</summary>
public static class NpmInstallLayout {
    /// <summary>Whether <paramref name="binaryPath"/> is the platform binary under
    /// <c>node_modules/@kurrent/kcap-&lt;platform&gt;/bin</c>, with the <c>@kurrent/kcap</c> launcher either beside its
    /// package or around it (npm nests the optional dependency inside the launcher's package on some layouts).</summary>
    public static bool IsPlatformBinary(string? binaryPath, Func<string, bool> fileExists) {
        var bin = Path.GetDirectoryName(binaryPath);
        if (string.IsNullOrEmpty(bin)) return false;

        return fileExists(Path.GetFullPath(Path.Combine(bin, "..", "..", "kcap", "bin", "kcap.js")))
            || fileExists(Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", "bin", "kcap.js")));
    }
}
