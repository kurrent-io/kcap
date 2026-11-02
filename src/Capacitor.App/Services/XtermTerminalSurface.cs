namespace Capacitor.App.Services;

using System.Text;
using SvcSystems.UI.Terminal;

/// Production ITerminalSurface wrapping SvcSystems.UI.Terminal's TerminalControlModel.
///
/// InputProduced fans in two sources, and a PTY round trip needs both: the model's UserInput
/// (keyboard and mouse) and the engine's DataReceived (terminal-originated replies such as a
/// cursor position report), which neither the SvcSystems wrapper nor the model re-expose.
public sealed class XtermTerminalSurface : ITerminalSurface {
    /// The VM-owned model handle the view binds.
    public TerminalControlModel Model { get; }

    public event Action<byte[]>? InputProduced;
    public event Action<int, int>? Resized;

    // The model exposes no Cols/Rows of its own; the wrapper's track every resize.
    public (int Cols, int Rows) CurrentSize => (Model.Terminal.Cols, Model.Terminal.Rows);

    // Either signal counts: the model's size event, or a size other than the placeholder.
    public bool Measured => _measured || CurrentSize != _placeholder;

    bool _measured;
    readonly (int Cols, int Rows) _placeholder;
    readonly string? _dumpPath;
    readonly string? _eventsPath;
    bool _replaying;

    /// A local agent's pane. ConPTY leaves a resize's rewrapping to the terminal, so on Windows the
    /// emulator reflows; elsewhere the agent redraws on SIGWINCH, and a reflow under that redraw
    /// duplicates rows.
    public static XtermTerminalSurface ForLocalPane(string? dumpPath) =>
        new(80, 24, dumpPath, reflowOnResize: OperatingSystem.IsWindows(), awaitsPaneSize: true);

    /// <paramref name="dumpPath"/>: a file every fed frame is appended to as received, before
    /// any rewriting — the only record of what the emulator was given. Sizes and attach
    /// snapshots go to a sibling <c>.events</c> file, each stamped with the dump's byte length
    /// at that moment.
    /// <paramref name="awaitsPaneSize"/>: <paramref name="cols"/> × <paramref name="rows"/> is a
    /// placeholder, and <see cref="Measured"/> stays false until the pane first sizes the model.
    public XtermTerminalSurface(int cols, int rows, string? dumpPath = null, bool reflowOnResize = false, bool awaitsPaneSize = false) {
        _dumpPath = dumpPath;
        _eventsPath = dumpPath is null ? null : dumpPath + ".events";
        _measured = !awaitsPaneSize;
        _placeholder = (cols, rows);
        Model = new TerminalControlModel(new TerminalOptions {
            Cols = cols,
            Rows = rows,
            ReflowOnResize = reflowOnResize,
        });

        // The control encodes keys with the legacy generators only, so the engine must not
        // advertise the kitty keyboard protocol: an agent that reads the advertisement expects
        // encodings the pane never sends.
        Model.Terminal.Engine.Options.KittyKeyboardEnabled = false;

        Model.UserInput += OnUserInput;
        Model.SizeChanged += OnSizeChanged;
        Model.Terminal.Engine.DataReceived += OnDataReceived;
        NoteDiagnostic($"surface {cols}x{rows} reflow={reflowOnResize} placeholder={awaitsPaneSize}");
    }

    /// Characters parsed per call: each piece's rewritten copy stays under the 85 KB large-object threshold.
    const int ParseChars = 32 * 1024;

    public void Feed(string text, bool replay = false) => Feed([text], replay);

    /// The body of the model's own Feed, minus its UTF-8 encode and decode of the whole text: on a
    /// reattach's megabytes of ring those two copies land on the large-object heap.
    public void Feed(IReadOnlyList<string> pieces, bool replay = false) {
        Model.SearchService?.Invalidate();
        var atBottom = Model.Terminal.Buffer.IsAtBottom;
        var previous = _replaying;
        _replaying = replay;
        try {
            foreach (var piece in pieces) {
                if (_dumpPath is not null) File.AppendAllText(_dumpPath, piece);
                for (var start = 0; start < piece.Length; start += ParseChars)
                    Model.Terminal.Feed(TerminalGlyphSubstitution.Apply(piece, start, Math.Min(ParseChars, piece.Length - start)));
            }
        } finally {
            _replaying = previous;
        }
        Model.UpdateDisplay();
        if (atBottom) Model.EnsureCaretIsVisible();
    }

    public void NoteDiagnostic(string line) {
        if (_eventsPath is null) return;
        try {
            var offset = File.Exists(_dumpPath) ? new FileInfo(_dumpPath!).Length : 0;
            File.AppendAllText(_eventsPath, $"{TimeProvider.System.GetUtcNow():O}\t@{offset}\t{line}\n");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Diagnostic only: an unwritable sidecar must not take the pane down.
        }
    }

    void OnUserInput(object? sender, TerminalUserInputEventArgs e) =>
        InputProduced?.Invoke(e.Data.ToArray());

    // The engine hands a reply back as a decoded string; the PTY side needs bytes.
    void OnDataReceived(object? sender, XTerm.Events.TerminalEvents.DataEventArgs e) {
        if (!_replaying) InputProduced?.Invoke(Encoding.UTF8.GetBytes(e.Data));
    }

    void OnSizeChanged(object? sender, TerminalSizeChangedEventArgs e) {
        _measured = true;
        NoteDiagnostic($"emulator {e.Cols}x{e.Rows}");
        Resized?.Invoke(e.Cols, e.Rows);
    }

    public void Resize(int cols, int rows) => Model.Terminal.Resize(cols, rows);
}
