#!/usr/bin/env python3
"""Seeds sessions shaped like real agent work for the desktop app to open: each turn is a prompt, assistant text, three
tool calls with their results and a closing reply, so the chat view renders every row kind it normally does.

Usage: seed_sessions.py TURNS [TURNS ...] [--url http://localhost:9473] [--wait 1800] [--kcap kcap] [--title T]
Each size becomes one Claude transcript under ~/.claude/projects, imported with `kcap import --claude`. Prints one JSON object per session
with its id and event_count once /api/sessions/<id>/summary has held steady for 30 s. About nine events a turn, so 20, 200 and
1500 turns give the audit's small, medium and large fixtures."""
import argparse
import json
import os
import subprocess
import tempfile
import time
import urllib.request
import uuid

TOOLS = [
    ("Bash", lambda i: {"command": f"dotnet test --filter Case{i}", "description": "Run the focused tests"},
     lambda i: f"Passed!  - Failed: 0, Passed: {i % 40 + 1}, Skipped: 0\n" + "  ok\n" * 12),
    ("Read", lambda i: {"file_path": f"/repo/src/Module{i % 17}/Service{i % 5}.cs"},
     lambda i: "\n".join(f"{n:>5}\tpublic void Method{n}() => Run({n});" for n in range(1, 40))),
    ("Edit", lambda i: {"file_path": f"/repo/src/Module{i % 17}/Service{i % 5}.cs",
                        "old_string": f"Run({i})", "new_string": f"RunAsync({i})"},
     lambda i: "The file has been updated."),
]


def stamp(n):
    return f"2026-11-01T{n // 3600 % 24:02d}:{n // 60 % 60:02d}:{n % 60:02d}.000Z"


def write_transcript(path, session, cwd, turns):
    clock = 0
    parent = None
    with open(path, "w") as f:
        def emit(kind, message, extra=None):
            nonlocal parent, clock
            clock += 2
            uid = str(uuid.uuid4())
            rec = {"type": kind, "uuid": uid, "parentUuid": parent, "sessionId": session, "cwd": cwd,
                   "timestamp": stamp(clock), "message": message}
            rec.update(extra or {})
            f.write(json.dumps(rec) + "\n")
            parent = uid

        for t in range(turns):
            emit("user", {"role": "user", "content": f"Turn {t}: tighten the retry policy in module {t % 17} and "
                                                     f"keep the tests green. " + "Context. " * 20})
            emit("assistant", {"role": "assistant", "id": f"msg_{t}_a", "model": "claude-test",
                               "content": [{"type": "text", "text": f"Looking at module {t % 17}. " +
                                            "The **retry** policy is spread over `Service` and `Client`.\n\n"
                                            "- first point\n- second point\n\n```csharp\nvar x = Retry(3);\n```\n" * 2}]})
            for k, (name, args, result) in enumerate(TOOLS):
                call = f"toolu_{t}_{k}"
                emit("assistant", {"role": "assistant", "id": f"msg_{t}_{k}", "model": "claude-test",
                                   "content": [{"type": "tool_use", "id": call, "name": name, "input": args(t)}]})
                emit("user", {"role": "user", "content": [{"type": "tool_result", "tool_use_id": call,
                                                           "content": result(t)}]})
            emit("assistant", {"role": "assistant", "id": f"msg_{t}_z", "model": "claude-test",
                               "content": [{"type": "text", "text": f"Done with turn {t}; tests pass."}]})


def summary(url, session):
    try:
        with urllib.request.urlopen(f"{url}/api/sessions/{session}/summary", timeout=10) as r:
            return json.load(r)
    except Exception:
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("turns", nargs="+", type=int)
    ap.add_argument("--url", default="http://localhost:9473")
    ap.add_argument("--wait", type=int, default=1800)
    ap.add_argument("--kcap", default="kcap")
    a = ap.parse_args()

    cwd = tempfile.mkdtemp(prefix="kcap-perf-")
    project = os.path.expanduser("~/.claude/projects/" + cwd.replace("/", "-"))
    os.makedirs(project, exist_ok=True)
    seeded = []
    for turns in a.turns:
        session = str(uuid.uuid4())
        transcript = os.path.join(project, f"{session}.jsonl")
        write_transcript(transcript, session, cwd, turns)
        seeded.append((session.replace("-", ""), turns, sum(1 for _ in open(transcript))))

    done = subprocess.run([a.kcap, "import", "--claude", "--folder", cwd, "--yes", "--no-update-check"],
                          env={**os.environ, "KCAP_URL": a.url}, capture_output=True, timeout=3600)
    if done.returncode != 0:
        raise SystemExit(f"kcap import exited {done.returncode}: {done.stderr.decode(errors='replace')}")

    incomplete = 0
    for sid, turns, lines in seeded:
        start = time.time()
        last, steady = None, 0
        while time.time() - start < a.wait:
            s = summary(a.url, sid)
            count = s and s.get("event_count")
            steady = steady + 1 if count and count == last else 0
            last = count
            # A line can project to more or fewer than one event, so done means the count held for 30 s.
            if steady >= 6:
                break
            time.sleep(5)
        settled = steady >= 6
        incomplete += not settled
        print(json.dumps({"session": sid, "turns": turns, "lines": lines, "event_count": last,
                          "ingest_s": round(time.time() - start, 1), "settled": settled}), flush=True)
    if incomplete:
        raise SystemExit(f"{incomplete} session(s) did not hold a steady event count within --wait")


if __name__ == "__main__":
    main()
