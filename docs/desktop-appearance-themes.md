# Desktop app: themes, fonts and a calmer layout

Branch `feature/desktop-soft-theme`. Work in progress, not reviewed for merge.

Visual study with every screenshot, the critique and the decisions:
https://kurrent.kcap.ai/pages/c3f4e9c0cbbb45b9a29c680b6013bf9f

## In one paragraph

The desktop app felt heavy: boxed panes, outlines on every filled shape, loud capital headings,
too much bright text and several saturated status colours at once. This branch adds a theme
picker to **Settings → Appearance** (theme, interface font, code font), keeps today's look as
**Classic**, and adds six "soft" themes that flatten the chrome and calm the layout: the chat
becomes one centred reading column, the side panes step back, and brightness, not weight or
colour, carries the hierarchy. Everything switches live, with no restart.

## Try it

```bash
git submodule update --init            # from kcap-server, if the cli submodule is fresh
dotnet build src/Capacitor.App/Capacitor.App.csproj -c Debug
src/Capacitor.App/bin/Debug/net10.0/kcap-desktop
```

A build run from its output folder is not a bundle, so it does not switch or replace an
installed kcap.app. It shares `~/.config/kcap` and the running daemon with the installed app.
Open Settings → Appearance and switch themes and fonts.

## What a user gets

| Setting | Options | Default on this branch |
|---|---|---|
| Theme | Classic, Graphite, Slate, Ink, Moss, Dusk, Paper (light, experimental) | Graphite |
| Interface font | Inter, Figtree, DM Sans | Figtree |
| Code font | Menlo, Source Code Pro, Roboto Mono | Source Code Pro |

Choices are saved in `app-state.json` (`Theme`, `UiFont`, `CodeFont`). Before merging, decide
whether the default should stay Graphite or go back to Classic.

## What the soft themes change

Classic is byte-for-byte the old look. The soft themes change:

**Chat (where people spend their time)**
- Messages, tool calls and the composer share one centred column, max 760px.
- Reply text 15.5px with 26px line height; the composer sits on the canvas, not a grey band.
- Tool groups are lines of activity, not filled cards; "Claude Code · 14:00" and "Command" are
  quiet labels, not bold headings.

**Header**
- Title over checkout; the status mark is hidden (the rail dot already says it).
- Stop is muted and turns red only on hover.

**Right pane (work context)**
- Sentence-case headings at normal weight; sections grouped by a 30px gap instead of rule lines.
- One bright title for the work item; key and state ("In flight") as small text, no pill.
- "Blocked by" is a line of text, not an orange box.
- People, plan tasks, PR title and detail values are grey; finished tasks fainter.
- PR status rows keep the coloured icon, words are grey; their chevrons appear on hover.

**Session list (rail)**
- Group names as written (`payments-api`), rows flush under their heading.
- Only the open session's title is bright; one weight for all titles.

**Everywhere**
- Filled shapes lose their outlines; header/footer rules go; fields show an edge only on focus.
- Rounder corners (cards 14, rows 10, controls 8–10); 140ms fades on hover and press.
- Scrollbars are a steady 6px grey pill (Fluent otherwise squeezes the thumb to a hairline).
- The launcher's "picked up" ring is grey instead of the accent blue.

## How it works

Most views bind the `Kcap*` brushes as `StaticResource` (about 1,100 uses), and a style declared
inside a view outranks any app-level style. Those two facts shape the design:

1. **Palette: recolour in place.** `AppearanceApplier` changes the `Color` of the existing
   `SolidColorBrush` instances in `App.axaml`, so every static binding updates live. Classic's
   colours are captured once per process before any theme touches them.
2. **App-level styles: one extra sheet.** `Views/SoftThemeStyles.axaml` is added to
   `Application.Styles` for soft themes and removed for Classic. It can only override styles that
   are themselves app-level (App.axaml, ChatBubbleStyles, MarkdownStyles, PullRequestStyles,
   SectionStyles).
3. **View-local styles: the `calm` window class.** For soft themes the applier puts `calm` on
   every window (`Window.WindowOpenedEvent` class handler covers new ones). Views add
   `Window.calm …` variants after their own rules, e.g. in `WorkContextView.axaml`,
   `SessionRailView.axaml`, `PullRequestStatusLabel.axaml`.
4. **Local attribute values: dynamic resources.** A local `Margin="…"` or `MaxWidth="…"` beats
   every style, so a few became `{DynamicResource …}` that the applier sets per theme:
   `KcapChatPresenterMargin`, `KcapChatMaxWidth`, `KcapAgentTextMaxWidth`,
   `KcapComposerBandBrush/Padding`, `KcapRailBodyPadding/Margin`, `KcapCardRadius`,
   `KcapSettingsCardRadius`, `KcapRowRadius`, `KcapFilledBorderBrush`, `KcapDividerBrush`,
   `KcapListTextBrush`, `KcapListTitleWeight`, `KcapMonoFont`.
5. **Fonts.** The interface font is a `TopLevel` style plus Fluent's
   `ContentControlThemeFontFamily` resource (Fluent's control themes read that key). The code
   font is the `KcapMonoFont` resource; the hard-coded Menlo stacks now point at it. Embedded
   fonts live in `Assets/Fonts/<Family>/` with their OFL licences.
6. **Heading case.** Avalonia has no text-transform, so `Views/Eyebrow.cs` adds
   `views:Eyebrow.Text`: capitals while `Eyebrow.Upper` is true (Classic), as written otherwise,
   sentence-casing an all-caps source. The soft sheet sets `Upper` false.

Gotchas found on the way:
- Values set inside a control template outrank plain styles; only a style with a pseudo-class
  (`:vertical`, `:pointerover`) beats them. The scrollbar thumb rules rely on that.
- A combined selector across two control types (`Button.x, Button.x /template/ ContentPresenter`)
  fails to compile when a setter targets a property only one of them has; split it.
- `AgentStatusMark` binds its own `IsVisible`, so the header hides it with `MaxWidth=0`.

## Files

| Area | Files |
|---|---|
| Model | `Services/AppTheme.cs` (palettes), `Services/AppFont.cs`, `Services/AppearanceChoice.cs` |
| Persistence | `Services/AppearanceSetting.cs`, `Services/AppStateStore.cs` (3 fields) |
| Applying | `Services/AppearanceApplier.cs`, wired in `App.axaml.cs` `Initialize` |
| Soft styles | `Views/SoftThemeStyles.axaml`, `Window.calm` rules in the views listed above |
| Settings UI | `Views/SettingsWindow.axaml` (Appearance tab), `ViewModels/AppearanceSettingsViewModel.cs`, `ViewModels/AppearanceOptionViewModel.cs` |
| Headings | `Views/Eyebrow.cs` |
| Fonts | `Assets/Fonts/**`, `Capacitor.App.csproj` (`AvaloniaResource`) |
| Tests | `AvaloniaSession.cs` pins Classic for the suite; `AppearanceGallery.cs`; `DocsShotScenes.cs` (`ShowcaseAsync`, `transcript` parameter); `DocsShotScenes.Windows.cs` (`appearance` parameter) |

## Screenshots

The gallery renders a demo session (markdown, code block, tool calls, a pending permission, the
full work context) once per theme and per font, plus the Appearance tab:

```bash
mkdir -p /tmp/gallery
KCAP_GALLERY_OUT=/tmp/gallery dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj \
  -- --treenode-filter "/*/*/AppearanceGallery/*"
```

It is skipped when `KCAP_GALLERY_OUT` is unset. The rest of the suite runs in Classic, so tests
that pin today's look keep passing.

## Adding a theme or a font

- Theme: add an entry to `AppTheme.All` (a dark one through `Dark(...)`; pass the status colours
  only where they differ). It appears in Settings automatically.
- Font: put the static TTFs (400/500/600/700) and the licence in `Assets/Fonts/<Family>/`, then
  add an `AppFont` with `avares://kcap-desktop/Assets/Fonts/<Family>#<Family Name>`. Check the
  family name inside the file: Fontsource's static files carry odd names ("Figtree Light");
  Google Fonts' own static TTFs are clean.

## Still to do

- Merge the PR's status rows into one line ("Changes requested · 1 thread · checks running").
- Fold Usage, Checkout and Session into one-line facts at the bottom of the right pane.
- People as one line ("Maya and Dana, active 2m ago").
- Rail's second line as "Claude · 12m · PR #88" with a single status dot.
- Narrower side panes (about 264 and 292px) so the chat gains width.
- The permission card as a question with Allow first.
- Paper: style the remaining Fluent controls; vendor icons need a light variant (Codex vanishes).
- Decide the default theme before merge; consider whether the font and theme should sync to the
  web app.
