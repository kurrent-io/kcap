namespace Capacitor.App.Services;

/// Sidebar never reaches the wire: it matches the titles of the rows the rail already holds.
public enum SearchMode { Sessions, Transcripts, Sidebar }

public static class SearchModeExtensions {
    public static string ToWire(this SearchMode mode) => mode switch {
        SearchMode.Sessions => "sessions",
        SearchMode.Transcripts => "transcripts",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };
}
