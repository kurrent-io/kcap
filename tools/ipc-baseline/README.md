# IPC baseline

Scripts that measure the paths between the desktop app, the daemon, the CLI and the server, so a change to any of
them can be checked against numbers instead of impressions. Python 3.9+, standard library only; run them on the
machine under test, usually a Tart VM with a local server on `http://localhost:9473` and the build under test
deployed.

| Script | Measures |
|---|---|
| `socket_probe.py [N]` | Daemon socket round trips: connect, `Hello`, first `DaemonStatus`, `List`, `AccountsSubscribe`, `ConsentRulesGet`. Guard: p50 ≤ 0.5 ms each. |
| `lifecycle.py daemon RUNS` | After a daemon restart: time to the first `Hello` answered and to a ready daemon. A daemon that never reports `starting` in its `HelloReply` reads ready at once, so its two times match. |
| `lifecycle.py server RUNS --label L` | After a local server restart: time to `/healthz/ready`. Run it under `tcpdump -i lo0 -w out.pcap port 9473` and read the capture with `pcap_hub.py` to see when each client's hub came back. |
| `pcap_hub.py CAPTURE` | Hub negotiates, WebSocket upgrades and server-sent invocations (`DaemonsChanged`) in a loopback capture, by time and source port. |
| `hook_latency.py` | Hook processes per agent event and their summed synchronous wall time, from the plugin manifest. |
| `http_baseline.py` | Endpoint latency on a warm connection and body size plain and compressed. |
| `seed_sessions.py LINES...` | A long transcript with its hooks fired back to back: how long SessionEnd takes and when the server holds every line. |
| `slow-claude` | A `claude` that answers `--version` after 20 s, for a daemon start behind a slow vendor probe. Link it as `claude` in a directory of its own and put that first on the daemon's `PATH`. |

Not covered yet: a fake vendor CLI the daemon can host, so the paths with a hosted agent running (PTY throughput,
status snapshots with agents, permission round trips) get numbers too; and a fixture of recorded real transcripts in
place of the synthetic ones.
