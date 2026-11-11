using System.Reflection;

namespace Capacitor.Cli.Core;

static class EmbeddedResources {
    static readonly Assembly Assembly = typeof(EmbeddedResources).Assembly;

    /// <summary>
    /// Loads an embedded resource by filename (e.g. "help-usage.txt").
    /// </summary>
    internal static string Load(string name) {
        var resourceName = $"Capacitor.Cli.Core.Resources.{name}";

        using var stream = Assembly.GetManifestResourceStream(resourceName)
         ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    /// <summary>
    /// Tries to load an embedded resource. Returns null if not found.
    /// </summary>
    internal static string? TryLoad(string name) {
        var resourceName = $"Capacitor.Cli.Core.Resources.{name}";

        using var stream = Assembly.GetManifestResourceStream(resourceName);

        if (stream is null) return null;

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    /// <summary>A help resource with its platform blocks resolved for this machine.</summary>
    internal static string? TryLoadHelp(string name) =>
        TryLoad(name) is { } text ? ForPlatform(text, OperatingSystem.IsWindows()) : null;

    /// <summary>Keeps the lines between <c>#if windows</c> (or <c>#if unix</c>) and <c>#endif</c> only on that
    /// platform; the marker lines themselves never print.</summary>
    internal static string ForPlatform(string text, bool windows) {
        var kept = new List<string>();
        var keep = true;

        foreach (var line in text.Split('\n')) {
            switch (line.TrimEnd('\r')) {
                case "#if windows": keep = windows; continue;
                case "#if unix":    keep = !windows; continue;
                case "#endif":      keep = true; continue;
            }

            if (keep) kept.Add(line);
        }

        return string.Join('\n', kept);
    }
}
