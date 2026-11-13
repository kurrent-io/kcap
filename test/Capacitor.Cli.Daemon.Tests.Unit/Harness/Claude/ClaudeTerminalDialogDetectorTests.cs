using System.Globalization;
using System.Text;
using Capacitor.Cli.Daemon.Harness.Claude;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// Pins dialog detection and the screen-only fallback when keystrokes cannot be inferred safely.
public class ClaudeTerminalDialogDetectorTests {
    // The first render, word by word at absolute columns as Claude Code draws it.
    static readonly string Dialog =
        "\x1b[?2004h\r\r\n\x1b[38;2;255;193;7m" + new string('─', 120) + "\x1b[39m\r\r\n" +
        "\x1b[3G\x1b[1mNew\x1b[7GMCP\x1b[11Gserver\x1b[18Gfound\x1b[24Gin\x1b[27Gthis\x1b[32Gproject:\x1b[41Gavalonia-docs\x1b[22m\r\r\n\r\r\n" +
        "\x1b[3GMCP\x1b[7Gservers\x1b[15Gmay\x1b[19Gexecute\x1b[27Gcode\x1b[32Gor\x1b[35Gaccess\x1b[42Gsystem\x1b[49Gresources.\x1b[60GAll tool calls require approval. Learn more in the MCP\r\r\n" +
        "\x1b[3Gdocumentation.\r\r\n\r\r\n" +
        "\x1b[5GUse\x1b[9Gthis\x1b[14GMCP\x1b[18Gserver\r\r\n" +
        "\x1b[5GUse\x1b[9Gthis\x1b[14Gand\x1b[18Gall\x1b[22Gfuture\x1b[29GMCP\x1b[33Gservers\x1b[41Gin\x1b[44Gthis\x1b[49Gproject\r\r\n" +
        "\x1b[3G\x1b[38;2;177;185;249m❯\x1b[5GContinue\x1b[14Gwithout\x1b[22Gusing\x1b[28Gthis\x1b[33GMCP\x1b[37Gserver\x1b[39m\r\r\n\r\r\n" +
        "\x1b[3G\x1b[3mEnter\x1b[9Gto\x1b[12Gconfirm\x1b[20G·\x1b[22GEsc\x1b[26Gto\x1b[29Gcancel\x1b[23m\r\r\n\x1b[2C\x1b[3A\x1b[>0q\x1b[?u\x1b[c";

    // The redraw after one Up arrow.
    const string CursorUp =
        "\x1b[2D\x1b[3B\r\x1b[2C\x1b[4A\x1b[38;2;177;185;249m❯\x1b[5GUse this and all future MCP servers in this project" +
        "\r\x1b[2C\x1b[1B\x1b[39m \x1b[5GContinue without using this MCP server\r\r\n\r\n\r\n\x1b[2C\x1b[4A";

    static readonly string Erase =
        "\x1b[2D\x1b[4B" + string.Concat(Enumerable.Repeat("\x1b[2K\x1b[1A", 12)) + "\x1b[2K\x1b[G";

    // The same dialog drawn in a 40-column PTY, where the heading and two of the labels wrap.
    const string Narrow =
        "\u001b[?2004h\u001b[?2031h\u001b[?1004h\r\r\n" +
        "\u001b[38;5;220m────────────────────────────────────────\u001b[39m\r\r\n" +
        "\u001b[3G\u001b[38;5;220m\u001b[1mNew\u001b[7GMCP\u001b[11Gserver\u001b[18Gfound\u001b[24Gin\u001b[27Gthis\u001b[22m\u001b[39m\r\r\n" +
        "\u001b[3G\u001b[38;5;220m\u001b[1mproject:\u001b[12Ga-server-with-a-rather-long\u001b[22m\u001b[39m\r\r\n" +
        "\u001b[3G\u001b[38;5;220m\u001b[1m-name-for-wrapping-tests\u001b[22m\u001b[39m\r\r\n" +
        "\r\r\n" +
        "\u001b[3GMCP\u001b[7Gservers\u001b[15Gmay\u001b[19Gexecute\u001b[27Gcode\u001b[32Gor\r\r\n" +
        "\u001b[3Gaccess\u001b[10Gsystem\u001b[17Gresources.\u001b[28GAll\u001b[32Gtool\r\r\n" +
        "\u001b[3Gcalls\u001b[9Grequire\u001b[17Gapproval.\u001b[27GLearn\u001b[33Gmore\r\r\n" +
        "\u001b[3Gin\u001b[6Gthe\u001b[10GMCP\u001b[14Gdocumentation.\r\r\n" +
        "\r\r\n" +
        "\u001b[5GUse\u001b[9Gthis\u001b[14GMCP\u001b[18Gserver\r\r\n" +
        "\u001b[5GUse\u001b[9Gthis\u001b[14Gand\u001b[18Gall\u001b[22Gfuture\u001b[29GMCP\u001b[33Gservers\r\r\n" +
        "\u001b[5Gin\u001b[8Gthis\u001b[13Gproject\r\r\n" +
        "\u001b[3G\u001b[38;5;153m❯\u001b[5GContinue\u001b[14Gwithout\u001b[22Gusing\u001b[28Gthis\u001b[33GMCP\u001b[39m\r\r\n" +
        "\u001b[5G\u001b[38;5;153mserver\u001b[39m\r\r\n" +
        "\r\r\n" +
        "\u001b[3G\u001b[38;5;246m\u001b[3mEnter\u001b[9Gto\u001b[12Gconfirm\u001b[20G·\u001b[22GEsc\u001b[26Gto\u001b[29Gcancel\u001b[23m\u001b[39m\r\r\n" +
        "\u001b[2C\u001b[4A\u001b[>0q\u001b[c";

    // The redraw after one Up arrow in that PTY, cut where the new cursor is drawn and the old one
    // not yet erased.
    const string NarrowCursorUpDrawn =
        "\u001b[2D\u001b[4B\r\u001b[2C\u001b[6A\u001b[38;5;153m❯\u001b[5GUse this and all future MCP servers \r\u001b[4C\u001b[1Bin this project";
    const string NarrowCursorUpErased =
        "\r\u001b[2C\u001b[1B\u001b[39m \u001b[5GContinue without using this MCP\u001b[K\r\u001b[4C\u001b[1Bserver\r\r\n\r\n\r\n\u001b[2C\u001b[6A";

    // An AskUserQuestion as Claude Code draws it over its spinner, at 120 columns.
    const string Question =
        "\x1b[2D\x1b[4B\r\x1b[8A\x1b[38;5;246m────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────\r\x1b[1B\x1b[48;5;153m\x1b[38;5;16m ☐ Codex round \x1b[39m\x1b[49m\x1b[K\r\x1b[2B\x1b[38;5;231m\x1b[1mCodex raised five more findings on the PR. How should I handle them?\x1b[22m\x1b[39m\x1b[K\r\x1b[1B\x1b[K\r\x1b[1B\x1b[38;5;153m❯\x1b[39m \x1b[38;5;246m1.\x1b[39m \x1b[38;5;153mFix the cheap ones (Recommended)\x1b[39m\x1b[K\r\x1b[2C\x1b[1B   \x1b[38;5;246mFix items 1, 2 and 5 and reply to the other two with reasons, without changing\x1b[85Gcode\x1b[90Gfor them.\x1b[39m\x1b[K\r\x1b[2C\x1b[1B\x1b[38;5;246m2.\x1b[39m Fix all five\x1b[K\r\r\n"
        + "\x1b[6G\x1b[38;5;246mAlso\x1b[11Gscan\x1b[16Gevery\x1b[22Gprocess\x1b[30Gand\x1b[34Gquery\x1b[40Glaunchd\x1b[48Gas\x1b[51Groot.\x1b[39m\r\r\n"
        + "\x1b[3G\x1b[38;5;246m3.\x1b[6G\x1b[39mStop\x1b[11Gthe\x1b[15Gloop\x1b[20Ghere\r\r\n"
        + "\x1b[6G\x1b[38;5;246mReply\x1b[12Gwithout\x1b[20Gchanging\x1b[29Gcode\x1b[34Gand\x1b[38Gstop\x1b[43Greview\x1b[50Grounds.\x1b[39m\r\r\n"
        + "\x1b[3G\x1b[38;5;246m4.\x1b[6GType\x1b[11Gsomething.\x1b[39m\r\r\n"
        + "\x1b[38;5;246m────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────\x1b[39m\r\r\n"
        + "\x1b[3G5.\x1b[6GChat\x1b[11Gabout\x1b[17Gthis\r\r\n"
        + "\r\r\n"
        + "\x1b[38;5;246mEnter\x1b[7Gto\x1b[10Gselect\x1b[17G·\x1b[19G↑/↓\x1b[23Gto\x1b[26Gnavigate\x1b[35G·\x1b[37GEsc\x1b[41Gto\x1b[44Gcancel\x1b[39m\r\r\n"
        + "\x1b[11A";

    // The redraw after one Down arrow.
    const string QuestionCursorDown =
        "\x1b(B\u000f\x1b[?2004h\x1b[11B\r\x1b[11A \x1b[6GFix the cheap ones (Recommended)\r\x1b[2B\x1b[38;5;153m❯\x1b[6GFix all five\x1b[39m\r\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\r\n\x1b[9A";

    static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Test]
    public async Task A_Bash_approval_with_an_amend_footer_uses_the_terminal_fallback_and_clears() {
        var screen = new ClaudeScreenWatcher();
        var approval = string.Join('\n',
            "  Look for kcap page commands",
            new string('─', 120),
            "  Bash command",
            "  Tip: auto mode handles these prompts for you — choose \"switch to auto mode\" below",
            "  Look for kcap page commands",
            new string('╌', 120),
            "  grep -ril \"pages/\" README.md src/Capacitor.Cli.Core/Resources/ 2>/dev/null | head; kcap --help 2>&1",
            "  | head -60",
            new string('╌', 120),
            "  This command requires approval",
            "",
            "  Do you want to proceed?",
            "  ❯ 1. Yes",
            "    2. Yes, and don't ask again for: kcap --help",
            "    3. Yes, and switch to auto mode · auto mode handles these prompts for you",
            "    4. No",
            "",
            "  Esc to cancel · Tab to amend");
        var split = approval.IndexOf("Tab to amend", StringComparison.Ordinal);
        await Assert.That(screen.Observe(Utf8(approval[..split])).Dialog).IsNull();

        var dialog = screen.Observe(Utf8(approval[split..])).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Heading).StartsWith("Bash command");
        await Assert.That(dialog.Options).IsEmpty();
        await Assert.That(dialog.Selected).IsEqualTo(-1);
        await Assert.That(dialog.Screen).Contains("grep -ril \"pages/\"");
        await Assert.That(dialog.Screen).Contains("This command requires approval");
        await Assert.That(dialog.Screen).Contains("4. No");

        var composer = string.Join('\n', "", new string('─', 120), "❯ ", new string('─', 120), "  ? for shortcuts");
        await Assert.That(screen.Observe(Utf8(composer)).Dialog).IsNull();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task An_auto_mode_settings_form_has_a_screen_but_no_single_choice_answers(int selected) {
        string[] settings = [
            "How you use Claude here     Mixed",
            "Also scan shell history     true",
            "Also scan your other repos  false",
        ];
        var form = string.Join('\n', new[] {
            new string('─', 120),
            "  Teach auto mode about your environment?",
            "",
            "  Claude Code reads this project, your recent Claude sessions, and optionally your shell history and other repositories.",
            "  Claude analyzes this data and customizes auto mode to make better decisions.",
            "",
        }.Concat(settings.Select((setting, i) => (selected == i ? "  ❯ " : "    ") + setting)).Concat([
            "",
            (selected == 3 ? "  ❯ " : "    ") + "Continue",
            "",
            "  ←/→ to change · Enter to continue · Esc to cancel",
        ]));
        var screen = new ClaudeScreenWatcher();
        var split = form.IndexOf("Enter to continue", StringComparison.Ordinal);
        await Assert.That(screen.Observe(Utf8(form[..split])).Dialog).IsNull();

        var dialog = screen.Observe(Utf8(form[split..])).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Heading).IsEqualTo("Teach auto mode about your environment?");
        await Assert.That(dialog.Options).IsEmpty();
        await Assert.That(dialog.Selected).IsEqualTo(-1);
        await Assert.That(dialog.Screen).Contains("Also scan shell history     true");
        await Assert.That(dialog.Screen).Contains("Continue");
        await Assert.That(screen.Observe(Utf8("\x1b[2Jready\r\n")).Dialog).IsNull();
    }

    [Test]
    public async Task A_dialog_is_read_by_its_layout_with_the_cursor_on_the_default() {
        var dialog = new ClaudeScreenWatcher().Observe(Utf8(Dialog)).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Heading).IsEqualTo("New MCP server found in this project: avalonia-docs");
        await Assert.That(dialog.Body).IsEqualTo("MCP servers may execute code or access system resources. All tool calls require approval. Learn more in the MCP documentation.");
        await Assert.That(dialog.Options.ToArray()).IsEquivalentTo(new[] {
            "Use this MCP server",
            "Use this and all future MCP servers in this project",
            "Continue without using this MCP server",
        }, CollectionOrdering.Matching);
        await Assert.That(dialog.Selected).IsEqualTo(2);
    }

    [Test]
    public async Task The_selection_follows_the_cursor_and_the_erased_dialog_clears() {
        var screen = new ClaudeScreenWatcher();
        screen.Observe(Utf8(Dialog));

        await Assert.That(screen.Observe(Utf8(CursorUp)).Dialog?.Selected).IsEqualTo(1);
        await Assert.That(screen.Observe(Utf8(Erase)).Dialog).IsNull();
    }

    /// ConPTY repaints an emptied row with erase-character and scrolls the rest away with delete-line, rather
    /// than the erase-line Claude itself sends; the dialog must clear either way.
    [Test]
    [Arguments("\x1b[{0};1H\x1b[120X")]
    [Arguments("\x1b[1;1H\x1b[30M")]
    public async Task A_dialog_erased_the_way_conpty_repaints_clears(string perRow) {
        var screen = new ClaudeScreenWatcher();
        await Assert.That(screen.Observe(Utf8(Dialog)).Dialog).IsNotNull();

        var erase = string.Concat(Enumerable.Range(1, 30).Select(row => string.Format(CultureInfo.InvariantCulture, perRow, row)));

        await Assert.That(screen.Observe(Utf8(erase)).Dialog).IsNull();
    }

    [Test]
    public async Task A_checkbox_list_comes_back_with_its_screen_and_no_choices() {
        var screen = string.Join('\n',
            new string('─', 40),
            "  2 new MCP servers found in this project",
            "",
            "  ❯ [ ] docs",
            "    [ ] search",
            "",
            "  Enter to confirm · Esc to cancel");

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, 120);

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("[ ] search");
    }

    /// A checkbox list is a finished menu, not a cursor redraw. Same heading or not, it must not
    /// keep the select choices the previous screen had, or Chat would send those keys to it.
    [Test]
    public async Task A_checkbox_dialog_does_not_keep_the_previous_select_choices() {
        var screen = new ClaudeScreenWatcher();
        var select = string.Join('\n',
            new string('─', 40),
            "  Pick a server",
            "",
            "    Use this one",
            "  ❯ Use the other",
            "",
            "  Enter to confirm · Esc to cancel");
        screen.Observe(Utf8(select));
        await Assert.That(screen.Observe(Utf8("")).Dialog!.Options).Count().IsEqualTo(2);

        var checkbox = "\x1b[2J" + string.Join('\n',
            new string('─', 40),
            "  Pick a server",
            "",
            "  ❯ [ ] docs",
            "    [ ] search",
            "",
            "  Enter to confirm · Esc to cancel");
        var dialog = screen.Observe(Utf8(checkbox)).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("[ ] search");
    }

    /// The cell count is the allocation. A winsize at the top of ushort wraps that product.
    [Test]
    public async Task A_resize_whose_cell_count_does_not_fit_keeps_the_dialog() {
        var screen = new ClaudeScreenWatcher(ushort.MaxValue, ushort.MaxValue);
        screen.Observe(Utf8(Dialog));
        await Assert.That(screen.Observe(Utf8("")).Dialog!.Options).Count().IsEqualTo(3);

        screen.Resize(ushort.MaxValue, ushort.MaxValue);
        await Assert.That(screen.Observe(Utf8("")).Dialog!.Options).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Same_column_wrapping_uses_the_screen_instead_of_guessing_choice_boundaries() {
        var screen = new ClaudeScreenWatcher(40, 40);

        var dialog = screen.Observe(Utf8(Narrow)).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Heading).StartsWith("New MCP server found in this project: a-server-with-a-rather-long");
        await Assert.That(dialog.Body).StartsWith("MCP servers may execute code or access system resources.");
        await Assert.That(dialog.Options).IsEmpty();
        await Assert.That(dialog.Selected).IsEqualTo(-1);
        await Assert.That(dialog.Screen).Contains("in this project");
        await Assert.That(dialog.Screen).Contains("Continue without using this MCP");
        await Assert.That(screen.Observe(Utf8(NarrowCursorUpDrawn)).Dialog!.Options).IsEmpty();
        await Assert.That(screen.Observe(Utf8(NarrowCursorUpErased)).Dialog!.Options).IsEmpty();
    }

    [Test]
    [Arguments(60, 0, 0)]
    [Arguments(60, 1, 0)]
    [Arguments(60, 2, 3)]
    [Arguments(120, 0, 3)]
    [Arguments(120, 1, 3)]
    [Arguments(120, 2, 3)]
    public async Task An_ambiguous_row_never_merges_the_decline_choice_into_an_approval(int cols, int selected, int count) {
        string[] labels = [
            "Use this MCP server",
            "Use this and all future MCP servers in this project",
            "Continue without using this MCP server",
        ];
        string Menu(int cursor) => string.Join('\n', new[] {
            new string('─', cols),
            "  New MCP server found in this project: docs",
            "",
        }.Concat(labels.Select((label, i) => (cursor == i ? "  ❯ " : "    ") + label)).Concat([
            "", "  Enter to confirm · Esc to cancel",
        ]));
        var screen = new ClaudeScreenWatcher();
        screen.Resize(cols, 40);
        await Assert.That(screen.Observe(Utf8(Menu(2))).Dialog!.Options).Count().IsEqualTo(3);

        var dialog = screen.Observe(Utf8("\x1b[2J" + Menu(selected))).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Options).Count().IsEqualTo(count);
        await Assert.That(dialog.Selected).IsEqualTo(count == 0 ? -1 : selected);
        await Assert.That(dialog.Screen).Contains(labels[1] + "\n");
        await Assert.That(dialog.Screen).Contains(labels[2]);
        if (count > 0)
            await Assert.That(dialog.Options.ToArray()).IsEquivalentTo(labels, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_chunk_that_ends_mid_redraw_keeps_the_readable_choices() {
        var screen = new ClaudeScreenWatcher();
        screen.Observe(Utf8(Dialog));
        var split = CursorUp.IndexOf("\r\x1b[2C\x1b[1B", StringComparison.Ordinal);

        var midway = screen.Observe(Utf8(CursorUp[..split])).Dialog;
        await Assert.That(midway!.Options).Count().IsEqualTo(3);
        await Assert.That(midway.Selected).IsEqualTo(2);

        await Assert.That(screen.Observe(Utf8(CursorUp[split..])).Dialog?.Selected).IsEqualTo(1);
    }

    [Test]
    [Arguments("Enter to confirm · Esc to cancel")]
    [Arguments("←/→ to change · Enter to continue · Esc to cancel")]
    [Arguments("Esc to cancel · Tab to amend")]
    public async Task Output_that_quotes_the_footer_is_not_a_dialog(string footer) {
        var quoted = string.Join('\n',
            new string('─', 40),
            "  The footer reads:",
            "  " + footer,
            "",
            new string('─', 40),
            "❯ ",
            new string('─', 40),
            "  ? for shortcuts");

        await Assert.That(ClaudeTerminalDialogDetector.Parse(quoted, 120)).IsNull();
    }

    [Test]
    [Arguments("Enter to confirm · Esc to cancel")]
    [Arguments("←/→ to change · Enter to continue · Esc to cancel")]
    [Arguments("Esc to cancel · Tab to amend")]
    public async Task A_footer_with_no_rule_above_it_is_not_a_dialog(string footer) {
        var screen = string.Join('\n', "  Use this one", "  ❯ Or this one", "", "  " + footer);

        await Assert.That(ClaudeTerminalDialogDetector.Parse(screen, 120)).IsNull();
    }

    // The fullscreen TUI's settings panel as /status draws it, at the daemon's 120 columns.
    static string Panel(params string[] rows) => string.Join('\n', new[] {
        "❯ /status",
        "",
        new string('▔', 120),
        "   Settings  Status   Config   Usage   Stats",
        "",
    }.Concat(rows));

    /// /status and /cost open a panel that writes nothing to the transcript, and a prompt pasted
    /// while it is open is swallowed. The panel takes no answer, so only its screen comes back.
    [Test]
    [Arguments("Esc to cancel")]
    [Arguments("Enter/Space to change · / to search · Esc to close")]
    [Arguments("Enter to set as default · s to use this session only · Esc to cancel")]
    public async Task A_fullscreen_panel_comes_back_with_its_screen_and_no_choices(string footer) {
        var screen = Panel(
            "   Version:           2.1.310",
            "   Session kind:      interactive",
            "   Model:             Default",
            "",
            "   " + footer);

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, 120);

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Heading).IsEqualTo("Settings  Status   Config   Usage   Stats");
        await Assert.That(dialog.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("Session kind:      interactive");
        await Assert.That(dialog.Screen).DoesNotContain(footer);
    }

    /// The usage tab outgrows a 40-row screen, so its footer is scrolled off and the last row is
    /// content ending in the scroll mark.
    [Test]
    public async Task A_panel_scrolled_past_its_footer_still_holds_the_keyboard() {
        var screen = Panel(
            "   Current session",
            "   █████████████████████                             42% used",
            "",
            "   Trim ~/repo/CLAUDE.md" + new string(' ', 80) + "↓");

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, 120);

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("Trim ~/repo/CLAUDE.md");
    }

    [Test]
    [Arguments("   Build finished ↓")]
    [Arguments("   ↓ Build details")]
    [Arguments("   ↓")]
    public async Task A_retained_panel_edge_does_not_make_ordinary_arrow_output_a_dialog(string output) {
        var screen = Panel("   Build output", output);

        await Assert.That(ClaudeTerminalDialogDetector.Parse(screen, 120)).IsNull();
    }

    [Test]
    [Arguments(40)]
    [Arguments(120)]
    public async Task A_padded_scroll_marker_can_arrive_after_the_panel_content(int cols) {
        var watcher = new ClaudeScreenWatcher(cols, 40);
        var screen = string.Join('\n', new string('▔', cols), "   Usage", "", "   More details          ↓");
        var arrow = screen.IndexOf('↓');

        await Assert.That(watcher.Observe(Utf8(screen[..arrow])).Dialog).IsNull();
        var dialog = watcher.Observe(Utf8(screen[arrow..])).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Screen).Contains("More details");
        await Assert.That(dialog.Options).IsEmpty();
    }

    [Test]
    [Arguments(120, 40)]
    [Arguments(40, 12)]
    public async Task A_short_external_rule_separates_a_select_dialog_from_a_retained_panel(int cols, int ruleLength) {
        var screen = string.Join('\n',
            new string('▔', cols),
            "   Settings  Status",
            new string('─', ruleLength),
            "  Pick a server",
            "",
            "    Use this one",
            "  ❯ Use the other",
            "",
            "  Enter to confirm · Esc to cancel");

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, cols);

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Heading).IsEqualTo("Pick a server");
        await Assert.That(dialog.Options.ToArray()).IsEquivalentTo(new[] { "Use this one", "Use the other" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task An_indented_separator_keeps_a_panel_list_screen_only() {
        var screen = Panel(
            "   Release notes",
            "   " + new string('─', 40),
            "   ❯ Version 2.1.310",
            "     Version 2.1.309",
            "",
            "   Enter to confirm · Esc to cancel");

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, 120);

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("Version 2.1.309");
    }

    /// A select menu whose footer names what Enter does, and adds keys of its own before Esc, is
    /// still read for its choices.
    [Test]
    public async Task A_menu_that_names_what_Enter_does_is_read_for_its_choices() {
        var screen = string.Join('\n',
            "❯ /model",
            "",
            new string('─', 120),
            "  Select model",
            "  Switch between Claude models. Your pick becomes the default for new sessions.",
            "",
            "  ❯ 1.  Default (recommended) ✔  Opus 5.5 · Best for everyday, complex tasks",
            "    2.  Sonnet 5.5               Most efficient for simpler tasks",
            "    3.  Haiku 5.5                Fastest for quick answers",
            "",
            "  ◐ Medium effort (default) ←/→ to adjust",
            "",
            "  Enter to set as default · s to use this session only · Esc to cancel");

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, 120);

        await Assert.That(dialog!.Heading).StartsWith("Select model");
        await Assert.That(dialog.Options).Count().IsEqualTo(3);
        await Assert.That(dialog.Options[2]).StartsWith("Haiku 5.5");
        await Assert.That(dialog.Selected).IsEqualTo(0);
    }

    /// A confirmation that ends at its last choice, with no footer, is still read for its choices;
    /// a numbered list in ordinary output, with the prompt box below it, is not a dialog.
    [Test]
    public async Task A_confirmation_without_a_footer_is_read_for_its_choices() {
        var confirm = string.Join('\n',
            "❯ /model",
            new string('─', 120),
            "  Switch model?",
            "  Your next response will be slower and use more tokens",
            "",
            "  This conversation is cached for the current model. Switching to Fable 5.2 means the full history gets re-read.",
            "",
            "  ❯ 1. Yes, switch to Fable 5.2",
            "    2. No, go back");
        var output = string.Join('\n',
            "● Two options:",
            "  ❯ 1. Keep it",
            "    2. Drop it",
            new string('─', 120),
            "❯ ",
            new string('─', 120),
            "  ⏵⏵ auto mode on (shift+tab to cycle)");

        var dialog = ClaudeTerminalDialogDetector.Parse(confirm, 120);

        await Assert.That(dialog!.Heading).IsEqualTo("Switch model? Your next response will be slower and use more tokens");
        await Assert.That(dialog.Options.ToArray()).IsEquivalentTo(new[] { "Yes, switch to Fable 5.2", "No, go back" }, CollectionOrdering.Matching);
        await Assert.That(dialog.Selected).IsEqualTo(0);
        await Assert.That(ClaudeTerminalDialogDetector.Parse(output, 120)).IsNull();
    }

    /// A panel whose footer offers only Esc holds the keyboard as any dialog does, and comes back
    /// as its screen.
    [Test]
    public async Task A_panel_that_only_closes_with_Esc_comes_back_as_its_screen() {
        var screen = string.Join('\n',
            "❯ /status",
            "",
            new string('─', 120),
            "  Status   Config   Usage",
            "",
            "  Version:           2.1.323",
            "  Model:             Default (Opus 5.5)",
            "",
            "  Esc to cancel");

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, 120);

        await Assert.That(dialog!.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("Version:           2.1.323");
    }

    /// The rows on screen are a window onto a list that scrolls, so they are not offered as the
    /// panel's whole set of answers even under a select footer.
    [Test]
    public async Task A_list_in_a_panel_comes_back_as_its_screen() {
        var screen = string.Join('\n',
            new string('▔', 120),
            "   Release notes",
            "",
            "   ❯ Version 2.1.310",
            "     Version 2.1.309",
            "     ↓ 402 more",
            "",
            "   Enter to confirm · Esc to cancel");

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, 120);

        await Assert.That(dialog!.Heading).IsEqualTo("Release notes");
        await Assert.That(dialog.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("Version 2.1.309");
    }

    /// A panel edge left in the scrollback is not the dialog: the prompt box below it, or a ruled
    /// dialog drawn after it, is what the screen shows now.
    [Test]
    public async Task A_panel_edge_above_the_prompt_box_is_not_a_dialog() {
        var idle = string.Join('\n',
            new string('▔', 120),
            "   Esc to cancel",
            new string('─', 120),
            "❯ ",
            new string('─', 120),
            "  Esc to cancel");
        var ruled = string.Join('\n',
            new string('▔', 120),
            "   Settings  Status",
            new string('─', 120),
            "  Pick a server",
            "",
            "    Use this one",
            "  ❯ Use the other",
            "",
            "  Enter to confirm · Esc to cancel");

        await Assert.That(ClaudeTerminalDialogDetector.Parse(idle, 120)).IsNull();
        var dialog = ClaudeTerminalDialogDetector.Parse(ruled, 120);
        await Assert.That(dialog!.Heading).IsEqualTo("Pick a server");
        await Assert.That(dialog.Options).Count().IsEqualTo(2);
    }

    [Test]
    public async Task A_question_is_read_with_each_choice_described_and_the_cursor_followed() {
        var screen = new ClaudeScreenWatcher();
        var dialog = screen.Observe(Utf8(Question)).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Heading).IsEqualTo("Codex raised five more findings on the PR. How should I handle them?");
        await Assert.That(dialog.Options.ToArray()).IsEquivalentTo(new[] {
            "Fix the cheap ones (Recommended) — Fix items 1, 2 and 5 and reply to the other two with reasons, without changing code for them.",
            "Fix all five — Also scan every process and query launchd as root.",
            "Stop the loop here — Reply without changing code and stop review rounds.",
            "Type something.",
            "Chat about this",
        }, CollectionOrdering.Matching);
        await Assert.That(dialog.Selected).IsEqualTo(0);

        await Assert.That(screen.Observe(Utf8(QuestionCursorDown)).Dialog?.Selected).IsEqualTo(1);
    }

    /// The rule above "Chat about this" is inside the dialog: taken for its top edge, it would leave
    /// that one row as the whole dialog.
    [Test]
    public async Task The_choice_under_the_questions_own_rule_is_still_a_choice() {
        var dialog = ClaudeTerminalDialogDetector.Parse(QuestionScreen(
            " ☐ Scope",
            "",
            "How far should the check reach?",
            "",
            "  1. Default folders",
            "  2. Type something.",
            new string('─', 120),
            "❯ 3. Chat about this",
            "",
            "Enter to select · ↑/↓ to navigate · Esc to cancel"), 120);

        await Assert.That(dialog!.Options.ToArray()).IsEquivalentTo(
            new[] { "Default folders", "Type something.", "Chat about this" }, CollectionOrdering.Matching);
        await Assert.That(dialog.Selected).IsEqualTo(2);
    }

    [Test]
    public async Task A_question_in_a_series_is_read_under_its_tab_row() {
        var dialog = ClaudeTerminalDialogDetector.Parse(QuestionScreen(
            "←  ☐ Scope  ☐ Replies  ✔ Submit  →",
            "",
            "How far should the check reach?",
            "",
            "❯ 1. Default folders",
            "     Only the default Claude and Codex folders.",
            "  2. Registered folders",
            "  3. Type something.",
            new string('─', 120),
            "  4. Chat about this",
            "",
            "Enter to select · Tab/Arrow keys to navigate · Esc to cancel"), 120);

        await Assert.That(dialog!.Heading).IsEqualTo("How far should the check reach?");
        await Assert.That(dialog.Options).Count().IsEqualTo(4);
        await Assert.That(dialog.Options[0]).IsEqualTo("Default folders — Only the default Claude and Codex folders.");
    }

    [Test]
    public async Task The_review_closing_a_series_is_read_though_it_has_no_footer() {
        var dialog = ClaudeTerminalDialogDetector.Parse(QuestionScreen(
            "←  ☒ Scope  ☒ Replies  ✔ Submit  →",
            "",
            "Review your answers",
            "",
            " ● How far should the check reach?",
            "   → Default folders",
            "",
            "Ready to submit your answers?",
            "",
            "❯ 1. Submit answers",
            "  2. Cancel"), 120);

        await Assert.That(dialog!.Heading).IsEqualTo("Review your answers");
        await Assert.That(dialog.Options.ToArray()).IsEquivalentTo(new[] { "Submit answers", "Cancel" }, CollectionOrdering.Matching);
    }

    /// Without the footer, only the series' tab row makes a numbered list the end of a question:
    /// a reply that happens to end on one, under a task checkbox, is output.
    [Test]
    public async Task A_numbered_list_ending_the_screen_without_a_tab_row_is_not_a_question() {
        var screen = QuestionScreen(
            " ☐ Write the tests",
            "",
            "❯ 1. Default folders",
            "  2. Cancel");

        await Assert.That(ClaudeTerminalDialogDetector.Parse(screen, 120)).IsNull();
    }

    [Test]
    public async Task A_multi_select_question_comes_back_as_its_screen() {
        var dialog = ClaudeTerminalDialogDetector.Parse(QuestionScreen(
            "←  ☐ Fixes  ✔ Submit  →",
            "",
            "Which findings should I fix?",
            "",
            "❯ 1. [ ] Item one",
            "         Homes from the account database.",
            "  2. [ ] Item two",
            "  3. [ ] Type something",
            "     Submit",
            new string('─', 120),
            "  4. Chat about this",
            "",
            "Enter to select · ↑/↓ to navigate · Esc to cancel"), 120);

        await Assert.That(dialog!.Heading).IsEqualTo("Which findings should I fix?");
        await Assert.That(dialog.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("[ ] Item two");
    }

    static string QuestionScreen(params string[] rows) =>
        string.Join('\n', new[] { "❯ earlier prompt", new string('─', 120) }.Concat(rows));
}
