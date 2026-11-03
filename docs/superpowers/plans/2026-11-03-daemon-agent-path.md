# Issue #2141: refresh macOS daemon agent discovery

Refresh PATH from the daemon user's own interactive login shell at startup and on a bounded periodic cadence. Reuse the existing timeout/fallback probe with a fresh instance per refresh; preserve the last usable PATH on failure. Never change identity or import shell credentials/configuration. Keep explicit vendor executable overrides and independent installation/recording signals intact.

Use a live environment binary probe only for the daemon, so its shared registry, availability checks and new child processes follow refreshed PATH. Re-evaluate supported vendors through the single-flight capability publication when availability changes, including agents installed after setup.

1. Add focused regressions for PATH changes and newly installed vendors; observe failures.
2. Implement bounded same-user PATH refresh and live daemon resolution; integrate vendor rediscovery.
3. Run affected tests and a controlled macOS shell/child-launch reproduction; inspect the diff.

Verification is scoped to affected test classes. The full suite remains for CI. No service reinstallation or changes to the user's running daemon are needed for verification.
