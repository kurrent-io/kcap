# In-app pages boundary probe

Checks, on a real machine, that a page shown by the desktop app's in-app viewer cannot reach the network, start WebRTC, or crash the app with a malformed URL. Written for Windows; the page runs unchanged on macOS.

1. `kcap experimental enable in-app-pages` (or Settings → Experimental in the app).
2. In a PowerShell window at the repository root: `powershell -ExecutionPolicy Bypass -File docs/probes/in-app-pages/listener.ps1`. It prints one `LEAK` line for each TCP connection to `127.0.0.1:8765` and each UDP datagram to `127.0.0.1:8766`.
3. Publish `probe.html` as a kcap page (ask an agent with the kcap plugin to publish this file with `publish_page`), and open it from the session's **Pages** tab.
4. Read the page's own lists, then click every item under **Click each one**.

Passing means all of these hold:

- The listener prints nothing: no img, fetch, XHR, WebSocket, beacon, prefetch, CSS, frame, nested frame, self-navigation, `window.open` or link request reached it.
- The page's own WebRTC line reads `undefined`, and the page reports that no peer connection constructor is reachable, so no STUN datagram or TURN connection reached the listener either. Its frames read `unreadable`: the page is sandboxed without `allow-same-origin`, so each frame it holds has an origin of its own. The app's own check, run before a page loads, reads those frames from the host document.
- Each link and button shows the Open / Dismiss prompt, or nothing for a malformed URL, and the app stays up. On Windows a malformed link that navigates the page's frame can leave Chromium's "content is blocked" page there: the content policy refused it before any request.
- If the app instead says pages open in the browser, its log (stderr) names which lock-down step failed: the viewer falls back rather than show the page unguarded.

Record the listener output and a screenshot of the page in the PR.
