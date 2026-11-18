using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap sharing</c> — who can open anything the server shares, changed one grant at a time. Every subcommand takes the
/// opaque object key the thing's own listing prints (<c>kcap page list</c> for a page) and passes it back as it came:
/// nothing here knows an entity type, so a type the server makes shareable later needs no change here.
/// </summary>
class SharingCommand(ISharingApi sharing) {
    const string Usage = """
        Usage:
          kcap sharing list <objectKey>
          kcap sharing candidates <objectKey> [query...]
          kcap sharing set <objectKey> <audienceKey> --relation <relation>
          kcap sharing remove <objectKey> <audienceKey>

        The object key comes from the thing's own listing, such as `kcap page list`. Audience keys and relations come
        from `candidates` (the default relation starred) or `list`; --relation takes the id in parentheses, or the relation as shown when it has none.
        """;

    public async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) return Fail(null);

        try {
            return args[1] switch {
                "list"       => await ListAsync(args),
                "candidates" => await CandidatesAsync(args),
                "set"        => await SetAsync(args),
                "remove"     => await RemoveAsync(args),
                _            => Fail($"Unknown sharing subcommand: {args[1]}")
            };
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ArtefactCommand.ForTerminal(ex.Message));
            return 1;
        } catch (ArgumentException ex) {
            return Fail(ex.Message);
        }
    }

    internal const string NotVisibleMessage =
        "Nothing under that object key is visible to this profile. Copy the key from the thing's own listing, such as `kcap page list`.";

    async Task<int> ListAsync(string[] args) {
        if (Parse(args) is not { } parsed) return 1;
        if (parsed.Positionals.Count != 1) return Fail("List takes exactly one object key.");

        switch (await sharing.GetAsync(parsed.Positionals[0])) {
            case SharingRead<SharingDto>.Found(var listed):
                foreach (var line in GrantLines(listed)) Console.WriteLine(line);
                return 0;

            default:
                await Console.Error.WriteLineAsync(NotVisibleMessage);
                return 1;
        }
    }

    /// <summary>One row per grant. A manager also sees what each grant may become and whether it may go; anyone else
    /// is told why they may not change it when the server says.</summary>
    internal static IEnumerable<string> GrantLines(SharingDto listed) {
        if (listed.Grants.Count == 0) {
            yield return "Shared with nobody you can see.";
        } else {
            var rows = listed.Grants.Select(g => new[] {
                Cell(g.Audience.Key), Kind(g.Audience.Kind), Cell(Relation(g.Relation)),
                listed.CanManage ? Join(g.Relations.Select(Relation)) : "-",
                listed.CanManage ? (g.Removable ? "yes" : "no") : "-",
                AudienceName(g.Audience)
            }).ToList();

            foreach (var line in Table(["AUDIENCE", "KIND", "RELATION", "CAN MOVE TO", "REMOVABLE", "NAME"], rows)) yield return line;
        }

        if (!listed.CanManage)
            yield return listed.ManageRefusal is { Length: > 0 } refusal
                ? Cell($"You cannot change who this is shared with: {refusal}")
                : "You can see who this is shared with, but not change it.";
    }

    async Task<int> CandidatesAsync(string[] args) {
        if (Parse(args) is not { } parsed) return 1;
        if (parsed.Positionals.Count < 1) return Fail("Candidates takes an object key, then words to look for.");

        var query = string.Join(' ', parsed.Positionals.Skip(1)).Trim();

        switch (await sharing.CandidatesAsync(parsed.Positionals[0], query)) {
            case SharingRead<SharingCandidatesDto>.Found({ Candidates.Count: 0 }):
                Console.WriteLine(query.Length == 0
                    ? "No candidates to suggest. Name someone to look for."
                    : Cell($"No candidates match \"{query}\"."));
                return 0;

            case SharingRead<SharingCandidatesDto>.Found(var found):
                foreach (var line in CandidateLines(found.Candidates)) Console.WriteLine(line);
                return 0;

            default:
                await Console.Error.WriteLineAsync(NotVisibleMessage);
                return 1;
        }
    }

    /// <summary>The default relation comes first and is starred, so <c>--relation</c> has an obvious choice.</summary>
    internal static IEnumerable<string> CandidateLines(IReadOnlyList<SharingCandidateDto> candidates) {
        var rows = candidates.Select(c => new[] {
            Cell(c.Audience.Key), Kind(c.Audience.Kind),
            Join(c.Relations.OrderBy(r => r.Relation == c.DefaultRelation ? 0 : 1)
                            .Select(r => r.Relation == c.DefaultRelation ? Relation(r) + "*" : Relation(r))),
            AudienceName(c.Audience)
        }).ToList();

        foreach (var line in Table(["AUDIENCE", "KIND", "RELATIONS", "NAME"], rows)) yield return line;

        yield return "* the default relation. Share with: kcap sharing set <objectKey> <audienceKey> --relation <id in parentheses, or as shown without the *>";
    }

    async Task<int> SetAsync(string[] args) {
        if (Parse(args, "--relation") is not { } parsed) return 1;
        if (parsed.Positionals.Count != 2) return Fail("Set takes an object key and one audience key.");
        if (parsed.Relation is not { Length: > 0 } relation)
            return Fail("Set needs --relation <relation>; `kcap sharing candidates` lists the relations each candidate may take.");

        var audience = parsed.Positionals[1];

        return Report(await sharing.ShareAsync(parsed.Positionals[0], audience, relation), $"{audience} now holds {relation}.");
    }

    async Task<int> RemoveAsync(string[] args) {
        if (Parse(args) is not { } parsed) return 1;
        if (parsed.Positionals.Count != 2) return Fail("Remove takes an object key and one audience key.");

        var audience = parsed.Positionals[1];

        return Report(await sharing.UnshareAsync(parsed.Positionals[0], audience), $"{audience} holds no grant now.");
    }

    /// <summary>A refusal is printed as the server worded it: the rules it names are the object's own, not the CLI's.</summary>
    static int Report(SharingWriteResult result, string applied) {
        switch (result) {
            case SharingWriteResult.Applied:
                Console.WriteLine(Cell(applied));
                return 0;

            case SharingWriteResult.Refused(var error):
                Console.Error.WriteLine(ArtefactCommand.ForTerminal(error.Message));
                return 1;

            default:
                Console.Error.WriteLine(NotVisibleMessage);
                return 1;
        }
    }

    sealed record Parsed(List<string> Positionals, string? Relation);

    /// <summary>Positionals, and <c>--relation</c> where the subcommand takes it. Any other flag, or <c>--relation</c>
    /// with no value, stops the command: read as absent, it would change a grant other than the one meant.</summary>
    static Parsed? Parse(string[] args, string? valueFlag = null) {
        var positionals = new List<string>();
        string? relation = null;

        for (var i = 2; i < args.Length; i++) {
            if (args[i] == "--") {
                positionals.AddRange(args[(i + 1)..]);
                break;
            }

            if (!args[i].StartsWith("--", StringComparison.Ordinal)) {
                positionals.Add(args[i]);
                continue;
            }

            if (args[i] != valueFlag) {
                Fail($"{args[i]} is not a flag of this command.");
                return null;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) {
                Fail($"{args[i]} needs a value.");
                return null;
            }

            relation = args[++i];
        }

        return new(positionals, relation);
    }

    /// <summary>A kind this binary does not know reads as <c>other</c>, as the server asks.</summary>
    static string Kind(string kind) => kind is "user" or "team" or "project" or "org" ? kind : "other";

    /// <summary>A name with its detail, marked when the audience is inactive. Names are other people's words.</summary>
    static string AudienceName(SharingAudienceDto audience) {
        var name = audience.Detail is { Length: > 0 } detail ? $"{audience.Name} ({detail})" : audience.Name;
        return Cell(audience.Inactive ? name + " [inactive]" : name);
    }

    /// <summary>A relation by the label a person reads, with the id <c>--relation</c> takes.</summary>
    static string Relation(SharingRelationDto relation) =>
        relation.Label is { Length: > 0 } label && label != relation.Relation ? $"{label} ({relation.Relation})" : relation.Relation;

    static string Join(IEnumerable<string> values) => values.ToList() is { Count: > 0 } list ? Cell(string.Join(", ", list)) : "-";

    /// <summary>Server text made inert and kept on one line.</summary>
    static string Cell(string text) => ArtefactCommand.ForTerminal(ArtefactCommand.OneLine(text));

    /// <summary>Left-aligned columns, the last one unpadded so a long name does not drag trailing spaces.</summary>
    static IEnumerable<string> Table(string[] headers, List<string[]> rows) {
        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();

        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => i == cells.Length - 1 ? c : c.PadRight(widths[i])));

        yield return Line(headers);
        foreach (var row in rows) yield return Line(row);
    }

    static int Fail(string? message) {
        if (message is not null) Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 1;
    }
}
