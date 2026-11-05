using System.Text.RegularExpressions;

namespace Capacitor.App.Services.Voice;

/// A reply read aloud a layer at a time. A short one is read whole; a long one opens with its
/// first sentence and the names of its parts, and the listener picks what to hear next. The parts
/// come from the reply's own structure — its headings, or its paragraphs when it has none — so
/// nothing is summarized by a model and nothing in the outline is invented.
public sealed partial class ReplyOutline {
    /// Spoken length a reply may have and still be read whole.
    public const int ShortLimit = 400;
    const int PartLength = 450;
    const int AllLimit = 3_000;

    readonly string _full;
    readonly bool _titled;
    int _next;

    ReplyOutline(string full, string lead, IReadOnlyList<ReplySection> sections, bool titled) {
        _full = full;
        Lead = lead;
        Sections = sections;
        _titled = titled;
    }

    public string Lead { get; }
    public IReadOnlyList<ReplySection> Sections { get; }
    public bool IsShort => _full.Length <= ShortLimit;

    /// Whether a part is still unread.
    public bool HasMore => !IsShort && _next < Sections.Count;

    public static ReplyOutline From(string markdown) {
        var full = SpeakableText.From(markdown, int.MaxValue);
        var (intro, titled) = Split(markdown);
        var sections = titled.Count >= 2 ? titled : Chunk(markdown);
        var leadSource = titled.Count >= 2 && intro.Length > 0 ? intro : full;
        return new ReplyOutline(full, FirstSentence(leadSource), sections, titled.Count >= 2);
    }

    /// What is said first.
    public string Opening() {
        if (IsShort) return _full;
        _next = 0;
        if (_titled) {
            var names = Sections.Select(s => s.Title).ToList();
            var list = names.Count == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";
            return $"{Lead} The reply has {names.Count} parts: {list}. Say read more, name a part, or read it all.";
        }
        return $"{Lead} It's a long reply, in {Sections.Count} parts. Say read more to go on, or read it all.";
    }

    /// The next unread part, or null when every part has been read.
    public string? Next() {
        while (!IsShort && _next < Sections.Count) {
            var spoken = Render(_next++);
            if (spoken is not null) return spoken;
        }
        return null;
    }

    /// The part before the one just read, and moves the cursor back to it.
    public string? Back() {
        if (IsShort) return null;
        _next = Math.Max(0, _next - 2);
        return Next();
    }

    /// What <see cref="Next"/> would say, without moving on.
    public string? PeekNext() {
        for (var index = _next; !IsShort && index < Sections.Count; index++) {
            if (Render(index) is { } spoken) return spoken;
        }
        return null;
    }

    string? Render(int index) {
        var section = Sections[index];
        var spoken = section.Spoken;
        // The opening already said the first sentence of an untitled reply.
        if (!_titled && index == 0 && spoken.StartsWith(Lead, StringComparison.Ordinal)) spoken = spoken[Lead.Length..].Trim();
        if (spoken.Length == 0) return null;
        var more = index + 1 < Sections.Count ? " Say more to go on." : " That's the end.";
        return (_titled ? $"{section.Title}. {spoken}" : spoken) + more;
    }

    /// The part a spoken name or number means, and moves the cursor past it.
    public string? Read(string query) {
        var index = FindIndex(query);
        if (index is not { } found) return null;
        _next = found;
        return Next();
    }

    public string All() => _full.Length <= AllLimit ? _full : SpeakableText.From(_full, AllLimit);

    public string PartNames => _titled
        ? string.Join(", ", Sections.Select(s => s.Title))
        : $"parts one to {Sections.Count}";

    int? FindIndex(string query) {
        var words = VoiceCommandParser.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("the" or "part" or "section" or "bit" or "about")).ToList();
        if (words.Count == 0) return null;
        if (words is ["last" or "final"]) return Sections.Count - 1;
        if (words.Count == 1 && Ordinal(words[0]) is { } ordinal) return ordinal < Sections.Count ? ordinal : null;
        if (!_titled) return null;
        var candidates = Sections.Select((s, i) => new VoiceSession(i.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Title, "", false)).ToList();
        return SessionMatcher.Best(string.Join(' ', words), candidates) is { } match
            ? int.Parse(match.AgentId, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    static int? Ordinal(string word) => word switch {
        "1" or "one" or "first" => 0,
        "2" or "two" or "second" => 1,
        "3" or "three" or "third" => 2,
        "4" or "four" or "fourth" => 3,
        "5" or "five" or "fifth" => 4,
        "6" or "six" or "sixth" => 5,
        "7" or "seven" or "seventh" => 6,
        "8" or "eight" or "eighth" => 7,
        _ => null,
    };

    // Headings, and lines that are nothing but bold text, which agents use as headings.
    static (string Intro, List<ReplySection> Sections) Split(string markdown) {
        var sections = new List<ReplySection>();
        var intro = "";
        string? title = null;
        var body = new List<string>();
        var inFence = false;

        void Close() {
            var text = string.Join('\n', body);
            if (title is null) intro = SpeakableText.From(text, int.MaxValue);
            else sections.Add(new ReplySection(title, SpeakableText.From(text, int.MaxValue)));
            body.Clear();
        }

        foreach (var line in markdown.Split('\n')) {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            var heading = inFence ? null : Heading().Match(line) is { Success: true } m ? m.Groups["t"].Value.Trim().TrimEnd(':') : null;
            if (heading is { Length: > 0 }) {
                Close();
                title = SpeakableText.From(heading, int.MaxValue);
                continue;
            }
            body.Add(line);
        }
        Close();
        return (intro, sections.Where(s => s.Spoken.Length > 0).ToList());
    }

    // Paragraphs, gathered into parts of about the same spoken length; a code block stays with its paragraph.
    static List<ReplySection> Chunk(string markdown) {
        var paragraphs = new List<string>();
        var current = new List<string>();
        var inFence = false;
        foreach (var line in markdown.Split('\n')) {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            if (!inFence && line.Trim().Length == 0) {
                if (current.Count > 0) paragraphs.Add(string.Join('\n', current));
                current.Clear();
                continue;
            }
            current.Add(line);
        }
        if (current.Count > 0) paragraphs.Add(string.Join('\n', current));

        var parts = new List<ReplySection>();
        var part = "";
        foreach (var spoken in paragraphs.Select(p => SpeakableText.From(p, int.MaxValue)).Where(p => p.Length > 0)) {
            part = part.Length == 0 ? spoken : $"{part} {spoken}";
            if (part.Length < PartLength) continue;
            parts.Add(new ReplySection($"part {parts.Count + 1}", part));
            part = "";
        }
        if (part.Length > 0) parts.Add(new ReplySection($"part {parts.Count + 1}", part));
        return parts;
    }

    static string FirstSentence(string text) {
        var match = SentenceEnd().Match(text);
        var sentence = match.Success && match.Index < 250 ? text[..(match.Index + 1)] : text;
        return sentence.Length <= 250 ? sentence.Trim() : sentence[..250].TrimEnd() + "…";
    }

    [GeneratedRegex(@"^\s{0,3}(#{1,6}\s+(?<t>.+?)\s*#*\s*$|(\*\*|__)(?<t>[^*_\n]{1,60})(\*\*|__):?\s*$)")]
    private static partial Regex Heading();

    [GeneratedRegex(@"[.!?](\s|$)")]
    private static partial Regex SentenceEnd();
}
