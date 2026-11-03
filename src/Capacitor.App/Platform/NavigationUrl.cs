using System.Diagnostics.CodeAnalysis;

namespace Capacitor.App.Platform;

/// The URL test the WebView package's navigation callbacks fail: they call new Uri(url), which
/// throws exactly when this refuses it, and web engines accept URLs System.Uri does not
/// (mailto:%, foo://a%zz/). A navigation guard cancels those before the package sees them.
public static class NavigationUrl {
    public static bool Parses(string? url) => TryParse(url, out _);

    public static bool TryParse(string? url, [NotNullWhen(true)] out Uri? uri) {
        uri = null;
        return url is not null && Uri.TryCreate(url, UriKind.Absolute, out uri);
    }
}
