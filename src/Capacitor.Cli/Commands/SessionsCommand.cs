using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Recording.PrDetection;
using Capacitor.Cli.Recording;

namespace Capacitor.Cli.Commands;

partial class SessionsCommand(
        ConfigRoot config, ProfileContext profiles, ICapacitorHttpClient http, GitProviderRouter router,
        WorkingDirectory workdir, TimeProvider time) {
    internal const string TimeFilterUnsupported = "The time filter needs a newer server; ask your admin to update.";

    internal const string NoRepository =
        "Not in a git repository with a remote origin. Pass --repo <owner/name>, or --repo all for every repository.";

    public async Task<int> HandleAsync(string[] args) {
        var options = SessionsArgs.Parse(args, time, out var error);

        if (options is null) {
            await Console.Error.WriteLineAsync($"kcap sessions: {error}");
            await Console.Error.WriteLineAsync(SessionsArgs.Usage);

            return 1;
        }

        var       baseUrl    = profiles.Resolution.ServerUrl!;
        using var httpClient = await http.ForCommandAsync();

        string? repoHash;
        string  label;

        if (options.AllRepos) {
            repoHash = null;
            label    = "any repository";
        } else if (options.Repo is null) {
            var repo = await RepositoryDetection.DetectRepositoryAsync(
                router,
                config, workdir.Path, time, detectPullRequest: false);

            if (repo?.Owner is null || repo.RepoName is null) {
                await Console.Error.WriteLineAsync(NoRepository);

                return 1;
            }

            var reported = false;
            repoHash = await RepositoryHashes.ForAsync(httpClient, baseUrl, repo.Owner, repo.RepoName, async response => {
                if (await HttpClientExtensions.HandleUnauthorizedAsync(response)) reported = true;
                else if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)) {
                    await Console.Error.WriteLineAsync($"The server could not resolve this repository ({(int)response.StatusCode}).");
                    reported = true;
                }
            }, onUnreachable: ex => {
                HttpClientExtensions.WriteUnreachableError(baseUrl, ex);
                reported = true;
            });
            label    = LocalRepository.Label(repo.Owner, repo.RepoName);

            if (repoHash is null) {
                if (!reported) await Console.Error.WriteLineAsync(RepositoryHashes.Unresolved);

                return 1;
            }
        } else {
            repoHash = options.RepoHash!;
            label    = options.Repo;
        }

        HttpResponseMessage resp;

        try {
            resp = await httpClient.GetWithRetryAsync(BuildUrl(baseUrl, repoHash, options), time);
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        if (await HttpClientExtensions.HandleUnauthorizedAsync(resp)) return 1;

        if (resp.StatusCode == HttpStatusCode.NotFound) {
            await Console.Error.WriteLineAsync("Session listing needs a newer server; ask your admin to update.");

            return 1;
        }

        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode) {
            await Console.Error.WriteLineAsync($"HTTP {(int)resp.StatusCode}: {body}");

            return 1;
        }

        RepoSessionsResponse? page;

        try {
            page = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.RepoSessionsResponse);
        } catch (JsonException) {
            page = null;
        }

        if (page?.Items is null) {
            await Console.Error.WriteLineAsync("Unexpected response from the server.");

            return 1;
        }

        if (IgnoredWindow(options, page)) {
            await Console.Error.WriteLineAsync(TimeFilterUnsupported);

            return 1;
        }

        await Console.Out.WriteAsync(options.Json
            ? body + Environment.NewLine
            : Render(page, label, options, StatusGlyphs.ConsoleEncodable, TerminalWidth()));

        return 0;
    }

    /// <summary>A server that does not know the window ignores it and answers with an unfiltered
    /// list, which carries no echo. Rendering that would show a result that looks filtered.</summary>
    internal static bool IgnoredWindow(SessionsOptions options, RepoSessionsResponse page) =>
        options.Windowed && page.Since is null && page.Until is null;

    internal static string BuildUrl(string baseUrl, string? repoHash, SessionsOptions options) {
        var route = repoHash is null
            ? $"{baseUrl}/api/sessions/listing"
            : $"{baseUrl}/api/repositories/{repoHash}/sessions";

        if (options.Cursor is { } cursor) return $"{route}?cursor={Uri.EscapeDataString(cursor)}&limit={options.Limit}";

        var qs = new List<string> { $"state={options.State}", $"limit={options.Limit}" };

        if (options.Mine) qs.Add("owner=me");

        if (options.Touching is { Length: > 0 } touching) qs.Add($"touching_path={Uri.EscapeDataString(touching)}");

        if (options.Since is { } since) qs.Add($"since={Uri.EscapeDataString(WhenParser.Format(since))}");

        if (options.Until is { } until) qs.Add($"until={Uri.EscapeDataString(WhenParser.Format(until))}");

        return $"{route}?" + string.Join("&", qs);
    }

    /// <param name="encodable">Whether the output carries an ellipsis; a cut cell ends in "..." when it
    /// does not, never in a look-alike the encoder picked.</param>
    /// <param name="width">The terminal's width, which the title is cut to fit; null leaves every title
    /// whole, as for redirected output.</param>
    internal static string Render(
            RepoSessionsResponse page, string repoLabel, SessionsOptions options, bool encodable = true, int? width = null) {
        var sb         = new StringBuilder();
        var showAccess = true;

        if (page.Items.Count == 0) {
            sb.AppendLine(Empty(repoLabel, options));
        } else {
            var rows = page.Items.Select(string?[] (row) => [
                    SessionIds.Display(row.SessionId),
                    row.Status == "active" && row.Stale ? "stale" : row.Status,
                    row.AccessLevel,
                    row.Owner?.Username ?? row.Owner?.UserId ?? "",
                    row.Vendor ?? "",
                    options.AllRepos ? Repository(row.Repo) : null,
                    row.Branch ?? "",
                    options.Windowed ? Local(row.StartedAt) : null,
                    Local(row.LastActivityAt),
                ])
                .ToList();

            string?[] heads = ["SESSION", "STATUS", "ACCESS", "OWNER", "VENDOR", options.AllRepos ? "REPO" : null, "BRANCH",
                options.Windowed ? "STARTED" : null, "LAST ACTIVITY"];
            int[] caps = [36, 7, 9, 14, 8, 24, 24, 16, 16];

            var widths = heads.Select((head, i) => head is null
                    ? 0
                    : Math.Min(Math.Max(caps[i], head.Length), Math.Max(head.Length, rows.Max(r => r[i]!.Length))))
                .ToArray();

            var titleOwnLine = Narrow(widths, heads, width);
            showAccess = heads.Contains("ACCESS");

            string Lead(IReadOnlyList<string?> cells) {
                var line = new StringBuilder();

                for (var i = 0; i < cells.Count; i++) {
                    if (heads[i] is null) continue;

                    line.Append(StatusGlyphs.Fit(cells[i]!, widths[i], encodable).PadRight(widths[i])).Append(' ');
                }

                return line.ToString();
            }

            var header = Lead(heads);

            sb.AppendLine(titleOwnLine && header.Length + "TITLE".Length > width - 1 ? header.TrimEnd() : header + "TITLE");

            for (var i = 0; i < rows.Count; i++) {
                var title = page.Items[i].Title ?? "(untitled)";

                if (titleOwnLine) {
                    sb.AppendLine(Lead(rows[i]).TrimEnd());
                    sb.AppendLine("  " + StatusGlyphs.Fit(title, Math.Max(FewestTitleColumns, width!.Value - 3), encodable));
                } else {
                    sb.AppendLine(Lead(rows[i]) + Title(title, header.Length, encodable, width));
                }
            }

            if (page.Total > page.Items.Count) sb.AppendLine(Overflow(page, options));
        }

        if (page.Items.Count > 0) {
            sb.AppendLine();
            if (showAccess) sb.AppendLine("ACCESS is what you may read of a session: overview, activity or full.");
            sb.AppendLine("Details (full access): kcap recap --full <session-id>");
        }

        if (page.NextCursor is { } next) sb.AppendLine(NextPage(options, repoLabel, next));

        return sb.ToString();
    }

    /// <summary>Only a period listing pages, so a listing without one already at the largest limit names that
    /// way rather than a --limit it cannot raise.</summary>
    static string Overflow(RepoSessionsResponse page, SessionsOptions options) {
        var showing = $"Showing {page.Items.Count} of {page.Total}";

        if (options.Windowed) return $"{showing} in this period.";

        return options.Limit < SessionsArgs.MaxLimit
            ? $"{showing}; raise --limit (up to {SessionsArgs.MaxLimit}), page by period with --since/--until, or narrow with --mine / --touching."
            : $"{showing}; page by period with --since/--until, which prints the next page's command.";
    }

    const int FewestTitleColumns = 12;

    /// <summary>Fits a row on the terminal, in order: columns from the owner, repository and branch, widest first;
    /// then whole columns a reader can spare; then the title moves to a line of its own. Ids, status and the last
    /// activity are never cut, since a cut one is useless. Returns whether the title has its own line.</summary>
    static bool Narrow(int[] widths, string?[] heads, int? width) {
        if (width is not { } columns) return false;

        int Excess(int title) => widths.Sum() + heads.Count(h => h is not null) + title - (columns - 1);

        Shrink(widths, heads, ["OWNER", "REPO", "BRANCH"], 10, () => Excess(FewestTitleColumns));

        foreach (var spare in new[] { "ACCESS", "OWNER", "VENDOR", "STARTED" }) {
            if (Excess(FewestTitleColumns) <= 0) return false;

            var i = Array.IndexOf(heads, spare);
            if (i < 0) continue;

            heads[i]  = null;
            widths[i] = 0;
        }

        if (Excess(FewestTitleColumns) <= 0) return false;

        Shrink(widths, heads, ["REPO", "BRANCH"], 6, () => Excess(0));

        return true;
    }

    static void Shrink(int[] widths, string?[] heads, string[] names, int fewest, Func<int> excess) {
        var columns = names.Select(name => Array.IndexOf(heads, name)).Where(i => i >= 0).ToArray();

        while (excess() > 0) {
            var widest = columns.Where(i => widths[i] > fewest).OrderByDescending(i => widths[i]).FirstOrDefault(-1);

            if (widest < 0) return;

            widths[widest]--;
        }
    }

    /// <summary>The title cut to the room left on a terminal line, so a row never wraps. A terminal
    /// too narrow to show a useful part of it gets it whole.</summary>
    static string Title(string title, int lead, bool encodable, int? width) {
        if (width is not { } columns || columns - 1 - lead < FewestTitleColumns) return title;

        return StatusGlyphs.Fit(title, columns - 1 - lead, encodable);
    }

    static string Empty(string repoLabel, SessionsOptions options) {
        if (options.Cursor is not null) return "No sessions on this page.";

        var what = options.State == "all" ? "sessions" : $"{options.State} sessions";
        var when = options.Windowed ? " in that period" : "";

        return $"No {what} visible to you on {repoLabel}{when}.";
    }

    // The line is meant to be pasted anywhere: it names the repository even when this run took it from
    // the checkout, and it is printed only from values a shell can read as nothing but arguments.
    static string NextPage(SessionsOptions options, string repoLabel, string cursor) {
        var repo = options.Repo ?? repoLabel;

        return Token().IsMatch(repo) && Token().IsMatch(cursor)
            ? $"More: kcap sessions --repo {repo} --cursor {cursor} --limit {options.Limit}"
            : "More: rows follow; read next_cursor with --json and pass it as --cursor.";
    }

    [GeneratedRegex("^[A-Za-z0-9_./=-]+$")]
    private static partial Regex Token();

    static string Repository(RepoSessionRepositoryDto? repo) =>
        repo switch {
            null                                 => "",
            { Owner: { } owner, Name: { } name } => $"{owner}/{name}",
            _                                    => repo.Hash
        };

    static string Local(DateTimeOffset instant) => instant.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    static int? TerminalWidth() {
        if (Console.IsOutputRedirected) return null;

        try {
            return Console.WindowWidth > 0 ? Console.WindowWidth : null;
        } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) {
            return null;
        }
    }
}
