using System.Xml;
using System.Xml.Linq;

namespace Capacitor.App.Platform.Windows;

/// A per-user Velopack install of the desktop app: <c>Update.exe</c> beside <c>current\</c>, which holds the app's
/// executable under the name its release shipped.
public static class VelopackRoot {
    public const string UpdateExe = "Update.exe";
    public const string AppExecutable = "kcap-desktop.exe";
    public const string LegacyAppExecutable = "Kurrent Capacitor.exe";

    public static string Content(string root) => Path.Combine(root, "current");

    /// The app's executable in <paramref name="root"/>, or null when root is not an install of the app.
    public static string? AppIn(string root) {
        try {
            if (!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null) return null;
            return new[] { AppExecutable, LegacyAppExecutable }.Select(name => Path.Combine(Content(root), name)).FirstOrDefault(File.Exists);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// The version Velopack recorded in <c>current\sq.version</c>, or null when it cannot be read.
    public static string? Version(string root) {
        try {
            using var reader = XmlReader.Create(Path.Combine(Content(root), "sq.version"),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            return XDocument.Load(reader).Descendants().FirstOrDefault(e => e.Name.LocalName == "version")?.Value.Trim() is { Length: > 0 } version
                ? version
                : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) {
            return null;
        }
    }
}
