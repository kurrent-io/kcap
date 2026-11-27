# KCAP desktop design system

The transcript is the reading surface. Session identity belongs in the rail, writing actions with the composer, and supporting evidence in work context. Use spacing and an explicit label to distinguish regions. Keep platform window controls, keyboard traversal and accessible names intact as the window narrows.

## Resources

| File under `Design/`                                              | Use                                                                                                                                                                                 |
| ----------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Palette.axaml`                                                   | Semantic canvas, surface, ink, field edge, status, selection and scrollbar brushes. Use the field edge for boundaries that identify an input and the quieter border for decoration. |
| `Themes.axaml`                                                    | The Light and Dark colors painted over the palette's brushes. Keep the same keys in both.                                                                                           |
| `Brand.axaml`                                                     | Vendor colors and onboarding artwork brushes. Use the existing icon controls for the marks.                                                                                         |
| `Typography.axaml` and `Text.axaml`                               | The embedded Inter type scale, platform monospace stack and shared text styles. Use `TypeScale` in C# for size values.                                                              |
| `Metrics.axaml`                                                   | Shared action, choice and chat-card insets, corner radii, minimum target size and disabled opacity. All lengths are device-independent.                                             |
| `Buttons.axaml`, `Fields.axaml`, `Overlays.axaml`, `Scroll.axaml` | Control variants and their interaction states. `App.axaml` loads these over Fluent in cascade order.                                                                                |

Each theme is paired with the matching Fluent variant. Changing only the application theme can put a light Fluent hover fill beneath a light KCAP label. Check all control states together when changing a theme. The opaque primary action has a dedicated foreground on every interaction fill.

## Adding a component

Start with a native control and the existing variant for the job. Use `kcapPrimary` for the primary action, `kcapGhost` for a quiet action, `kcapField` for a standalone input and `kcapPanel` for a themed flyout. Add a new semantic token when multiple controls need to change together. Share a control's hover, pressed, disabled and keyboard-focus treatments in its style. The visible text of a status should name the state even in grayscale.

Use Lucide through `AppIcon`. Keep its 12, 14 or 16 DIP artwork inside a target at least 24 DIP on each axis. Give icon-only controls an accessible name. Keep normal text at 4.5:1 or higher and meaningful focus and control boundaries at 3:1 or higher against their actual backgrounds.

Run `tools/ui-audit/contrast.py`, `type_scale.py` and `axaml_a11y.py`. Exercise changed interactions with the corresponding Avalonia smoke tests. Compare a synthetic workspace at the 1200×560 minimum and 1400×760 default, including keyboard focus, the composer and a PR with a failing check.

## Themes

Under Settings → Appearance, the Auto, Light and Dark tiles show the same miniature workspace. Auto splits the illustration down the middle. Auto is the default and follows the system's light or dark setting, live. Explicit choices stay fixed. The scheme is saved for this computer and applies across profiles. The app paints it before its first window, so the install, update, uninstall and startup-error dialogs open in it too.

`Themes.axaml` contains paired Light and Dark color dictionaries. `DesktopPalette` validates every entry before applying a change, recolors the shared brush instances held by existing controls, and selects the corresponding Fluent variant. Keep the same keys in both dictionaries. `Palette.axaml` declares those brushes without colors, so each color lives in one place; `App.Initialize` paints Light as soon as the resources load, and the app paints the saved or system scheme before its first window. Add theme-dependent UI to the semantic palette. Keep the terminal's authored ANSI colors and the onboarding artwork on their intended backgrounds.

| Role                                | Light                 | Dark                  |
| ----------------------------------- | --------------------- | --------------------- |
| Reading canvas / supporting surface | `#FAF9FB` / `#FFFFFF` | `#202123` / `#262729` |
| Primary / secondary text            | `#171320` / `#625C70` | `#E2E2E0` / `#BDBEBD` |
| Selected row / selection ink        | `#E5EDFC` / `#315AA3` | `#304151` / `#86B9F0` |
| Primary action / label              | `#6D1E40` / `#FFFFFF` | `#D6D6D1` / `#262726` |

Segmented controls and sidebar filter dropdowns share neutral hover and pressed fills. Selection keeps the blue fill, with a semibold label on the active workspace tab. Higher contrast strengthens borders independently of these fills.

The work-context pane has no left border of its own, so the resize grabber's line is the only divider. Dark uses graphite surfaces, soft neutral text, a pearl primary action and slate-blue selection.

In Dark, use emerald `#64D998` for success, amber `#EBB64F` for attention, coral `#FF9087` for failure and violet `#C6A0F6` for the purple status role. Each of the five semantic inks, including blue, has at least 5.80:1 contrast across the base surfaces and its own tinted fill. On a selected row, the minimum is 4.79:1 for the danger label. The audit checks every status ink on the selection fill. Bookmarks keep their user-selected colors.

Status is colored by role. Work in progress (`KcapActivity*`) uses neutral ink rather than purple, so it does not read as a merged PR; a started task's dot and an unknown outcome's hollow ring tell it apart from its neighbours by shape. PR lifecycle follows GitHub: open green, merged violet, closed red, draft muted.

On the graphite canvas, primary text measures 12.42:1 and secondary text 8.64:1. The keyboard focus outline uses the primary text token around the entire tile. Selection uses the blue outline around the miniature and a semibold caption. At 150% zoom on macOS each tile occupies 171 × 150 device-independent pixels, including its caption.

Appearance offers Comfortable and Compact density. Compact reduces padding and row gaps while preserving minimum targets. Transcript prose uses 24 DIP line height in Comfortable and 22 DIP in Compact. Density and font changes remeasure visible rows and preserve the user's reading anchor. Inter is the default reading face. Monospace uses embedded Spline Sans Mono with platform fallbacks and leaves code and terminal metrics intact. Higher contrast strengthens text and edges and honors system contrast when the OS reports it. These preferences apply across profiles.

## Transcript message metadata

Right-aligned inset prompts and left-aligned assistant prose distinguish roles by shape and placement. Ordinary author labels are hidden. When the transcript identifies a different participant, their name stays visible beside their prompt.

A reply is everything the agent shows between one prompt and the next. Its messages, tool calls, thinking and notes all belong to it, including notes the agent answers on its own, such as a finished background task, a subagent's report, another agent's or session's message, or a trigger, so a sequence the agent carries on by itself keeps one footer at its end. Its timestamp and Copy sit once, aligned left, after its last row, whatever that row is, so a message the agent continues from with a tool call keeps no space for them. A question card or topic header is never the last row, and neither is a row folded into a topic. Copy puts the reply's messages on the clipboard in order, separated by blank lines, as rich text with Markdown source as plain text. Notes stay out of the copy, since they are not part of the answer and read as noise when pasted. A turn of tool calls alone has nothing to copy and no footer. A prompt carries its own timestamp and Copy under its bubble, aligned right. As rows stream in, the footer moves to the reply's new last row. A whitespace-only message adds no row.

Hover any row of a reply, or a prompt's bubble or the row under it, or move keyboard focus into one, to reveal its timestamp, marks and Copy. The pointer can move from the row onto them. A click hands them back to the pointer, even on a control that already has focus, and a key press returns them to the keyboard. The timestamp reads the time alone for today, Yesterday or the weekday within the past week, and the date before that; its tooltip and the accessibility metadata carry the full sender and time. The context menu opens by right click, Shift+F10 or touch long press. It lists the copy actions first, Copy image, Copy link, Copy selection and Copy message, which copies that one message, then Select all, then a rule and the bookmark actions. A prompt of attachments alone has no menu of its own. The time, marks and Copy share one look: 12 DIP icons in 24 × 24 ghost targets 2 DIP apart, with 12 DIP before the timestamp.

In sessions that support bookmarks and topics, the bookmark and topic toggles share that row. Read from the outer edge, right for a prompt and left for a reply, it holds Copy, then the bookmark, then the topic, then the timestamp. A reply's toggles mark its first message, so its topic header lands above the reply and its bookmark leads to where it starts. A mark already on a later message of the reply still shows in the bookmark list and topic header. Existing marks remain visible and actionable at rest. The row reserves space for every control, so revealing them keeps prose and link positions stable.

The footer sits inside the bottom gap of the row it follows and adds its own height there, 28 DIP with its 4 DIP gap; the row's gap follows the footer. In a 1400 × 760 synthetic workspace in Light Comfortable, including the item gaps, an “ok” prompt measures 94 DIP, and a one-line assistant message inside and ending a reply 44 and 72 DIP. Rows hold their bounds on hover.

When the viewport moves, a row's container can serve another item with the same template: it keeps the row and rebinds it, after clearing the old message's selection, context-menu state and drag and find marks. A row holding focus or a pressed pointer is never recycled. Prompt and reply rows park attached and hidden, up to 12, so taking one back skips styling a fresh row; resetting the feed releases them.

Inline and fenced code keep their own typography when switching reading faces.

## GitHub links in chat

A github.com pull request or issue address in a prompt or reply shows as a chip: the pull request reader's lifecycle glyph, or the issue circle, then `owner/repo#n`, then Open, Draft, Merged or Closed once the state is known, in the PR lifecycle tones. Until then, and when the state cannot be read, the chip is the neutral glyph and label. The state comes from the PRs linked to listed sessions, else from the signed-in GitHub CLI, one lookup at a time for chips on screen; without a signed-in CLI chips stay neutral. A chip opens in the browser, shows its address on hover, takes a keyboard stop with the other links and copies as its address. A link with a label of its own, another GitHub page, an Enterprise host and `#123` shorthand stay ordinary links or text.

## Baseline measurements

Pilot questions remain around recognition of lightly filled selections, separation of raised surfaces in Light, and distinguishing a filled destructive action from a primary action. Test those with readers on Windows, macOS and Linux.

Compare Light and Dark with the same native synthetic transcript. Compare startup, typing, scrolling and jumps independently.

## Session and linked-work tooltips

Show the full title first and place the state directly below it across desktop appearances. Keep the inactivity explanation with the Stale status. Set each semibold metadata label immediately above its value. Align labels, values and wrapped text to the same left edge. Omit missing values. Add a plain browser note when an external work-context link only shows the item title or key. The PR card shows check results in their own row. A tooltip whose only content is the label text opens only when the label is truncated. Focus a structured tooltip target and press Shift+F1 to show its details, or Escape to dismiss them. In an open PR chooser, Escape dismisses the tooltip and keeps the highlighted choice available. Switching applications dismisses the tooltip.
