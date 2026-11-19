# Desktop app performance

Scripts that time what a desktop-app user waits for: cold start, opening a session, idle cost and memory. They run
inside a macOS guest (usually a Tart VM) logged in to its console session, against a local server on
`http://localhost:9473` with the build under test installed as `/Applications/kcap.app`. Python 3.9+ standard library,
Bash, and the two Swift helpers below. Numbers from a 4 GB guest are a baseline to compare builds against in that
guest, not a prediction for a laptop.

## Setup

```bash
swiftc -O ocr.swift -o ocr       # Vision OCR, used to read when text appeared on screen
swiftc -O click.swift -o click   # synthetic left click at a point
python3 seed_sessions.py 20 200 1500
```

`seed_sessions.py` writes Claude transcripts shaped like real work (prompt, text, three tool calls with results,
reply) and imports them with `kcap import --claude`: about nine events a turn, so `20 200 1500` gives sessions of
184, 1,804 and 13,505 events. Give the guest's terminal Screen Recording permission once; macOS asks for it on the
first `screencapture` and ignores synthetic clicks on that prompt.

Stamp every build with the same version, so swapping one bundle for another does not start the post-update flow,
and discard the first launch after a swap anyway: it may finish an update and hold the main window on purpose.
Run one flow at a time with nothing else running in the guest, and keep the host quiet too: a build on the host takes
CPU from the guest.

## Flows

| Script | Measures |
|---|---|
| `coldstart.sh TAG RUNS SECONDS` | Quits and relaunches the app RUNS times. Per run: a screenshot loop, CPU seconds at 10 s, and the `app-lifecycle.jsonl` lines the launch wrote. `KCAP_PERF_FRAMES=0` drops the screenshot loop for a build that writes `window_shown` and `first_sessions_painted`, so the loop does not take CPU from the launch it times. |
| `coldstart_ab.sh RUNS SECONDS TAG=BUNDLE ...` | Cold starts several builds in turn, one launch each per round, renaming each bundle into `/Applications` for its launch, so load from outside the guest falls on every build alike. |
| `coldstart_report.py DIR` | Per run and as medians: when the window and the session rows first appeared on screen, as a range from the frame before to the frame that showed them, plus every lifecycle mark (`since_start_ms`) and the `startup_phases` durations. |
| `view_load.sh TAG X Y SECONDS` | Clicks a rail row at (X, Y) in screen points and records a screenshot loop and the app's CPU over the run. |
| `view_report.py DIR --transcript "re:turn 1499\D"` | When the transcript's last message appeared. A needle starting with `re:` is a regular expression; OCR misreads punctuation, so anchor on letters and digits, and pick text only the session under test shows. |
| `capture_open.sh TAG X Y SECONDS` | `view_load.sh` under a loopback capture, then one line per HTTP request: route, Accept-Encoding, status, Content-Encoding, body bytes, and requests per route. Needs passwordless `sudo` for `sh`, `tcpdump` and `kill`. |
| `pcap_http.py CAPTURE [PORT]` | The parser `capture_open.sh` uses, for a capture taken some other way. |
| `idle_cpu.sh LABEL SECONDS` | CPU share of one core, idle wakeups per second (the difference between `top`'s two `idlew` samples) and memory footprint before and after. |

Output lands under `~/app-perf-out/`. A session-open run starts after a restart and 20 s of idle, so the first open
pays for whatever the app builds lazily.
