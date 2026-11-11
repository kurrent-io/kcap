using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Views;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// The search page as drawn: snippet runs, and the signed-out prompt in place of the list.
public class SessionSearchViewSmokeTests {
    /// Opens on the sidebar tab; Tab moves to the server's Sessions tab.
    static void OpenWithShortcut(SearchWindowHost host) {
        host.Window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Meta);
        host.Settle();
        host.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        host.Settle();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Shortcut_opens_on_the_sidebar_tab_and_lists_matching_rail_sessions_without_a_request() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("a") with { Title = "Fix the login timeout" });
        host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("b") with { Title = "Tidy README" });
        host.Settle();

        host.Window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Meta);
        host.Settle();
        host.Window.KeyTextInput("THE");
        host.SettleSearch();

        var titles = host.Named<SessionSearchView>("SearchCover").GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Name == "SearchHitTitle")
            .Select(t => string.Concat((t.Inlines ?? []).OfType<Run>().Select(r => r.Text)))
            .ToArray();

        await Assert.That(host.Vm.Search!.Tab).IsEqualTo(SearchMode.Sidebar);
        await Assert.That(titles).IsEquivalentTo(["Fix the login timeout"]);
        await Assert.That(host.Requests).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Matched_snippet_runs_render_bold_without_markers() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        host.Response = new SearchResponseDto {
            Sessions = [new() {
                SessionId = "S1", Slug = "fix-auth", Title = "Fix auth flow", StartedAt = "2026-10-29T09:00:00Z", EndedAt = "2026-10-29T10:00:00Z",
                MatchedField = "title", Snippet = "Fix \u0002auth\u0003 flow", PrNumber = 42,
            }],
        };
        OpenWithShortcut(host);

        host.Window.KeyTextInput("auth");
        host.SettleSearch();

        var title = host.Named<SessionSearchView>("SearchCover").GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "SearchHitTitle");
        var runs = (title.Inlines ?? []).OfType<Run>().ToArray();
        var text = string.Concat(runs.Select(r => r.Text));
        var matched = runs.Single(r => r.Text == "auth");
        var matchBrush = (IBrush)Application.Current!.FindResource("KcapInfoBrush")!;
        var rendered = string.Join('\n', host.Named<SessionSearchView>("SearchCover").GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Run>().Select(r => r.Text)) : t.Text ?? ""));

        var chip = host.Named<SessionSearchView>("SearchCover").GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SearchHitPrChip");
        var gap = chip.TranslatePoint(default, title)!.Value.X - title.Bounds.Width;

        await Assert.That(text).IsEqualTo("Fix auth flow");
        await Assert.That(gap).IsLessThanOrEqualTo(6.5);
        await Assert.That(rendered).DoesNotContain('\u0002');
        await Assert.That(rendered).DoesNotContain('\u0003');
        await Assert.That(rendered).Contains("#42");
        await Assert.That((int)matched.FontWeight).IsGreaterThanOrEqualTo((int)FontWeight.SemiBold);
        await Assert.That(matched.Foreground).IsSameReferenceAs(matchBrush);
        await Assert.That(runs.Where(r => r != matched).All(r => r.FontWeight < FontWeight.SemiBold)).IsTrue();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_long_title_is_trimmed_and_keeps_the_pr_chip_and_time_in_the_row() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        var title = string.Join(' ', Enumerable.Repeat("authentication", 40));
        host.Response = new SearchResponseDto {
            Sessions = [new() {
                SessionId = "S1", Slug = "fix-auth", Title = title, StartedAt = "2026-10-29T09:00:00Z", EndedAt = "2026-10-29T10:00:00Z",
                MatchedField = "title", Snippet = title, PrNumber = 42,
            }],
        };
        OpenWithShortcut(host);

        host.Window.KeyTextInput("auth");
        host.SettleSearch();

        var cover = host.Named<SessionSearchView>("SearchCover");
        var titleBlock = cover.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SearchHitTitle");
        var chip = cover.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SearchHitPrChip");
        var row = titleBlock.FindAncestorOfType<Button>()!;
        var when = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == host.Vm.Search!.Hits[0].WhenText);
        double Right(Visual v) => v.TranslatePoint(new Point(v.Bounds.Width, 0), row)!.Value.X;
        double Left(Visual v) => v.TranslatePoint(new Point(0, 0), row)!.Value.X;
        var untrimmed = new TextBlock { Text = title, FontSize = titleBlock.FontSize };
        untrimmed.Measure(Size.Infinity);

        await Assert.That(titleBlock.Bounds.Width).IsLessThan(untrimmed.DesiredSize.Width);
        await Assert.That(chip.IsEffectivelyVisible).IsTrue();
        await Assert.That(chip.Bounds.Width).IsGreaterThan(0);
        await Assert.That(Right(titleBlock)).IsLessThanOrEqualTo(Left(chip));
        await Assert.That(Right(chip)).IsLessThanOrEqualTo(Left(when));
        await Assert.That(Right(when)).IsLessThanOrEqualTo(row.Bounds.Width);
    });

    /// Only the hits on screen get a row, and the keyboard highlight scrolls the rows it moves onto into view.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_long_hit_list_builds_only_the_visible_rows_and_follows_the_highlight() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        host.Response = new SearchResponseDto {
            Sessions = [.. Enumerable.Range(0, 200).Select(i => new SessionSearchHitDto {
                SessionId = $"S{i}", Slug = $"s-{i}", Title = $"Fix auth {i}", StartedAt = "2026-10-29T09:00:00Z",
                EndedAt = "2026-10-29T10:00:00Z", MatchedField = "title", Snippet = $"Fix \u0002auth\u0003 {i}",
            })],
        };
        OpenWithShortcut(host);
        host.Window.KeyTextInput("auth");
        host.SettleSearch();

        var cover = host.Named<SessionSearchView>("SearchCover");
        var list = host.Named<ItemsControl>("SearchHits");
        await Assert.That(host.Vm.Search!.Hits.Count).IsEqualTo(200);
        await Assert.That(cover.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("searchHit"))).IsLessThan(50);

        for (var i = 0; i < 120; i++) host.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        host.Settle();

        await Assert.That(host.Vm.Search.HighlightIndex).IsEqualTo(120);
        var row = list.ContainerFromIndex(120);
        await Assert.That(row).IsNotNull();
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var top = row!.TranslatePoint(default, scroll)!.Value.Y;
        await Assert.That(top).IsGreaterThanOrEqualTo(-0.5);
        await Assert.That(top + row.Bounds.Height).IsLessThanOrEqualTo(scroll.Viewport.Height + 0.5);
    });

    /// New results reset the highlight to the first hit, and the list scrolls back so the hit Enter opens is on screen.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task New_results_after_a_deep_scroll_bring_the_first_hit_back_into_view() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        host.Response = new SearchResponseDto {
            Sessions = [.. Enumerable.Range(0, 200).Select(i => new SessionSearchHitDto {
                SessionId = $"S{i}", Slug = $"s-{i}", Title = $"Fix auth {i}", StartedAt = "2026-10-29T09:00:00Z",
                EndedAt = "2026-10-29T10:00:00Z", MatchedField = "title", Snippet = $"Fix auth {i}",
            })],
        };
        OpenWithShortcut(host);
        host.Window.KeyTextInput("auth");
        host.SettleSearch();
        for (var i = 0; i < 120; i++) host.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        host.Settle();

        host.Window.KeyTextInput("s");
        host.SettleSearch();
        host.Settle();

        var list = host.Named<ItemsControl>("SearchHits");
        await Assert.That(host.Vm.Search!.HighlightIndex).IsEqualTo(0);
        var row = list.ContainerFromIndex(0);
        await Assert.That(row).IsNotNull();
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var top = row!.TranslatePoint(default, scroll)!.Value.Y;
        await Assert.That(top).IsGreaterThanOrEqualTo(-0.5);
        await Assert.That(top + row.Bounds.Height).IsLessThanOrEqualTo(scroll.Viewport.Height + 0.5);
    });

    /// A wheel scroll under a still pointer raises no PointerMoved, yet the highlight must not stay behind off screen
    /// for Enter: the row the pointer rests on once the scroll lands takes it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_wheel_scroll_moves_the_highlight_to_the_row_under_the_pointer() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        host.Response = new SearchResponseDto {
            Sessions = [.. Enumerable.Range(0, 200).Select(i => new SessionSearchHitDto {
                SessionId = $"S{i}", Slug = $"s-{i}", Title = $"Fix auth {i}", StartedAt = "2026-10-29T09:00:00Z",
                EndedAt = "2026-10-29T10:00:00Z", MatchedField = "title", Snippet = $"Fix auth {i}",
            })],
        };
        OpenWithShortcut(host);
        host.Window.KeyTextInput("auth");
        host.SettleSearch();

        var list = host.Named<ItemsControl>("SearchHits");
        var center = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), host.Window)!.Value;
        host.Window.MouseWheel(center, new Vector(0, -20));
        host.Settle();

        var index = host.Vm.Search!.HighlightIndex;
        await Assert.That(index).IsGreaterThan(0);
        var row = list.ContainerFromIndex(index);
        await Assert.That(row).IsNotNull();
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var top = row!.TranslatePoint(default, scroll)!.Value.Y;
        await Assert.That(top).IsGreaterThanOrEqualTo(-0.5);
        await Assert.That(top + row.Bounds.Height).IsLessThanOrEqualTo(scroll.Viewport.Height + 0.5);
    });

    /// An avatar is read as the person it stands for, in the rail and on a search hit, never as their initials.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Owner_avatars_speak_the_owners_name_not_their_initials() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        host.Directory.Rows.AddOrUpdate(AgentRow.FromServer(new RepoSessionDto {
            SessionId = "S2", Status = "active", AccessLevel = "full", Vendor = "claude", Title = "Tidy the rail",
            Owner = new() { UserId = "u2", DisplayName = "Yuki Tanaka" },
            StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch,
            Repo = new() { Hash = "h", Owner = "kurrent-io", Name = "kcap-cli" },
        }, hostedPresentation: false));
        host.Settle();
        var rail = ControlView.Descendants(host.Window.GetVisualDescendants().OfType<SessionRailView>().Single())
            .Select(peer => peer.GetName()).ToList();

        host.Response = new SearchResponseDto {
            Sessions = [new() {
                SessionId = "S1", Slug = "fix-auth", Title = "Fix auth flow", StartedAt = "2026-10-29T09:00:00Z", EndedAt = "2026-10-29T10:00:00Z",
                MatchedField = "title", Snippet = "Fix auth flow", OwnerDisplayName = "Wendy Ng", OwnerUser = "wendy",
            }],
        };
        OpenWithShortcut(host);
        host.Window.KeyTextInput("auth");
        host.SettleSearch();
        var hit = host.Named<SessionSearchView>("SearchCover").GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "SearchHitTitle").FindAncestorOfType<Button>()!;
        var search = ControlView.Descendants(hit).Select(peer => peer.GetName()).ToList();

        await Assert.That(rail).Contains("Yuki Tanaka");
        await Assert.That(rail).DoesNotContain("YT");
        await Assert.That(search).Contains("Wendy Ng");
        await Assert.That(search).DoesNotContain("WN");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Signed_out_page_shows_sign_in_and_sends_no_request() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost(signedOut: true);
        OpenWithShortcut(host);
        await Assert.That(host.Vm.SignInVisible).IsTrue();
        await Assert.That(host.Vm.SearchShowsSignIn).IsTrue();
        await Assert.That(host.Named<TextBox>("SearchQueryBox").IsReadOnly).IsTrue();

        host.Window.KeyTextInput("auth flow");
        host.SettleSearch();
        var prompt = host.Named<StackPanel>("SearchSignInPrompt");
        var promptShown = prompt.IsEffectivelyVisible;
        var listShown = host.Named<DockPanel>("SearchResults").IsVisible;
        host.Named<Button>("SearchSignInButton").Command!.Execute(null);

        await Assert.That(host.Vm.IsSearchOpen).IsTrue();
        await Assert.That(promptShown).IsTrue();
        await Assert.That(listShown).IsFalse();
        await Assert.That(host.Requests).IsEmpty();
        await Assert.That(host.SignIns).IsEqualTo(1);
    });

    /// A 401 to the page shows the prompt but leaves the query editable; signing back in asks again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_401_shows_sign_in_then_signing_in_asks_again_and_shows_results() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        host.Response = new SearchResponseDto {
            Sessions = [new() { SessionId = "S1", Slug = "fix-auth", Title = "Fix auth", StartedAt = "2026-10-29T09:00:00Z", MatchedField = "title", Snippet = "Fix auth" }],
        };
        host.Outcome = ServerReadOutcome.Unauthorized;
        OpenWithShortcut(host);
        host.Window.KeyTextInput("auth");
        host.SettleSearch();
        var promptAfter401 = host.Named<StackPanel>("SearchSignInPrompt").IsVisible;
        var editable = !host.Named<TextBox>("SearchQueryBox").IsReadOnly;

        host.Lane.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));
        host.Settle();
        var readOnlySignedOut = host.Named<TextBox>("SearchQueryBox").IsReadOnly;
        host.Outcome = ServerReadOutcome.Ok;
        host.Lane.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
        host.Settle();
        host.SettleSearch();

        await Assert.That(promptAfter401).IsTrue();
        await Assert.That(editable).IsTrue();
        await Assert.That(readOnlySignedOut).IsTrue();
        await Assert.That(host.Requests.Count).IsEqualTo(2);
        await Assert.That(host.Named<StackPanel>("SearchSignInPrompt").IsVisible).IsFalse();
        await Assert.That(host.Named<DockPanel>("SearchResults").IsVisible).IsTrue();
        await Assert.That(host.Vm.Search!.Hits.Count).IsEqualTo(1);
    });
}
