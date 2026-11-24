# KCAP desktop design system

The transcript is the reading surface. Session identity belongs in the rail, writing actions with the composer, and supporting evidence in work context. Use spacing and an explicit label to distinguish regions. Keep platform window controls, keyboard traversal and accessible names intact as the window narrows.

## Resources

| File under `Design/`                                              | Use                                                                                                                                                                                 |
| ----------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Palette.axaml`                                                   | Semantic canvas, surface, ink, field edge, status, selection and scrollbar brushes. Use the field edge for boundaries that identify an input and the quieter border for decoration. |
| `Brand.axaml`                                                     | Vendor colors and onboarding artwork brushes. Use the existing icon controls for the marks.                                                                                         |
| `Typography.axaml` and `Text.axaml`                               | The embedded Inter type scale, platform monospace stack and shared text styles. Use `TypeScale` in C# for size values.                                                              |
| `Metrics.axaml`                                                   | Shared action, choice and chat-card insets, corner radii, minimum target size and disabled opacity. All lengths are device-independent.                                             |
| `Buttons.axaml`, `Fields.axaml`, `Overlays.axaml`, `Scroll.axaml` | Control variants and their interaction states. `App.axaml` loads these over Fluent in cascade order.                                                                                |

The shipped palette is paired with Fluent Dark. Changing only the application theme can put a light Fluent hover fill beneath a light KCAP label. Check all control states together when introducing a palette. The opaque primary action has a dedicated foreground on every interaction fill.

## Adding a component

Start with a native control and the existing variant for the job. Use `kcapPrimary` for the primary action, `kcapGhost` for a quiet action, `kcapField` for a standalone input and `kcapPanel` for a themed flyout. Add a new semantic token when multiple controls need to change together. Share a control's hover, pressed, disabled and keyboard-focus treatments in its style. The visible text of a status should name the state even in grayscale.

Use Lucide through `AppIcon`. Keep its 12, 14 or 16 DIP artwork inside a target at least 24 DIP on each axis. Give icon-only controls an accessible name. Keep normal text at 4.5:1 or higher and meaningful focus and control boundaries at 3:1 or higher against their actual backgrounds.

Run `tools/ui-audit/contrast.py`, `type_scale.py` and `axaml_a11y.py`. Exercise changed interactions with the corresponding Avalonia smoke tests. Compare a synthetic workspace at the 1200×560 minimum and 1400×760 default, including keyboard focus, the composer and a PR with a failing check.

## Desktop redesign preview

**Desktop redesign preview** is on by default. Turn it off under App-wide in Settings → Experimental to restore the original appearance. Under Appearance, the Auto, Light and Dark tiles show the same miniature workspace. Auto splits the illustration down the middle. Auto is the default and follows system changes while the preview is open. Explicit choices stay fixed. The scheme is saved for this computer and applies across profiles. Disabling the experiment keeps the saved scheme for the next opt-in.

`ClassicPreview.axaml` contains paired color dictionaries. The palette validates every entry before applying a change, updates the shared brush instances held by existing controls, and selects the corresponding Fluent variant. Rollback restores the captured colors and Fluent variant. Keep the same keys in both dictionaries. Add theme-dependent UI to the semantic palette. Keep the terminal's authored ANSI colors and the onboarding artwork on their intended backgrounds.

| Role                                | Classic Light         | Classic Dark          |
| ----------------------------------- | --------------------- | --------------------- |
| Reading canvas / supporting surface | `#FAF9FB` / `#FFFFFF` | `#202123` / `#262729` |
| Primary / secondary text            | `#171320` / `#625C70` | `#E2E2E0` / `#BDBEBD` |
| Selected row / selection ink        | `#E5EDFC` / `#315AA3` | `#304151` / `#86B9F0` |
| Primary action / label              | `#6D1E40` / `#FFFFFF` | `#D6D6D1` / `#262726` |

Segmented controls and sidebar filter dropdowns share neutral hover and pressed fills. Selection keeps the blue fill, with a semibold label on the active workspace tab. Higher contrast strengthens borders independently of these fills.

The work-context pane uses its resize grabber as the divider with the preview enabled. PR lifecycle, health text, and work-in-progress keep their existing meanings during the comparison. The dark preview uses graphite surfaces, soft neutral text, a pearl primary action and slate-blue selection. Modern and transcript interaction experiments have separate flags.

In Dark, use emerald `#64D998` for success, amber `#EBB64F` for attention, coral `#FF9087` for failure and violet `#C6A0F6` for the purple status role. Each of the five semantic inks, including blue, has at least 5.80:1 contrast across the base surfaces and its own tinted fill. On a selected row, the minimum is 4.79:1 for the danger label. The audit checks every status ink on the selection fill. Bookmarks keep their user-selected colors.

On the graphite canvas, primary text measures 12.42:1 and secondary text 8.64:1. The keyboard focus outline uses the primary text token around the entire tile. Selection uses the blue outline around the miniature and a semibold caption. At 150% zoom on macOS each tile occupies 171 × 150 device-independent pixels, including its caption.

Appearance offers Comfortable and Compact density. Compact reduces padding and row gaps while preserving minimum targets. Transcript prose uses 24 DIP line height in Comfortable and 22 DIP in Compact. Density and font changes remeasure visible rows and preserve the user's reading anchor. Inter is the default reading face. Monospace uses embedded Spline Sans Mono with platform fallbacks and leaves code and terminal metrics intact. Higher contrast strengthens text and edges and honors system contrast when the OS reports it. These preferences apply across profiles.

Pilot questions remain around recognition of lightly filled selections, separation of raised surfaces in Light, and distinguishing a filled destructive action from a primary action. Test those with readers on Windows, macOS and Linux before widening the preview.

Compare the stock app, preview-off and default appearance under Light and Dark with the same native synthetic transcript. Compare startup, typing, scrolling and jumps independently.

## Zen mode

Zen temporarily hides the rail and inspector without changing saved pane preferences. The Exit Zen action remains in pane traversal and keeps the attention badge. Exiting restores the panes that fit the current width and returns focus to the previous target when it remains available.
