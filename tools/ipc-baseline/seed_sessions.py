#!/usr/bin/env python3
"""Writes a synthetic Claude transcript and fires the session's hooks without waiting, the way a long session resumed
and closed quickly does; then polls the server until it holds every line, or gives up.

Usage: seed_sessions.py LINES [LINES ...] [--url http://localhost:9473] [--wait 900] [--kcap kcap]
For each size it writes LINES transcript lines under a throwaway project directory, runs SessionStart, one
UserPromptSubmit, Stop and SessionEnd back to back through the commands kcap/hooks/hooks.json gives those events,
records how long the SessionEnd hook took, and reports when /api/sessions/<id>/summary's event_count reached the line count. Each line is one user or
assistant message, so each projects to one event."""
import argparse
import json
import os
import shlex
import subprocess
import tempfile
import time
import urllib.request
import uuid


def write_transcript(path, session, cwd, lines):
    with open(path, "w") as f:
        parent = None
        for i in range(lines):
            msg_id = str(uuid.uuid4())
            role = "user" if i % 2 == 0 else "assistant"
            content = f"synthetic line {i} " + "x" * 600
            record = {"type": role, "uuid": msg_id, "parentUuid": parent, "sessionId": session, "cwd": cwd,
                      "timestamp": f"2026-11-01T00:{i // 3600 % 60:02d}:{i // 60 % 60:02d}.{i % 1000:03d}Z",
                      "message": {"role": role, "content": content} if role == "user" else
                                 {"role": role, "id": f"msg_{i}", "model": "claude-test", "content": [{"type": "text", "text": content}]}}
            f.write(json.dumps(record) + "\n")
            parent = msg_id


HOOKS_JSON = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "kcap", "hooks", "hooks.json")


def plugin_commands(kcap):
    """Each event's unmatched hook commands from the plugin manifest, with `kcap` swapped for the binary under test, so
    the baseline drives the hooks the plugin really runs."""
    with open(HOOKS_JSON) as f:
        manifest = json.load(f)["hooks"]
    commands = {}
    for event, groups in manifest.items():
        for group in groups:
            if "matcher" in group:
                continue
            for entry in group["hooks"]:
                argv = shlex.split(entry["command"])
                commands.setdefault(event, []).append([kcap, *argv[1:]])
    return commands


def hook(commands, payload, cwd, url):
    event = payload["hook_event_name"]
    if event not in commands:
        raise SystemExit(f"{HOOKS_JSON} has no {event} hook")
    start = time.perf_counter()
    for argv in commands[event]:
        # KCAP_URL sends the hooks to the server this script polls, whatever profile is active.
        done = subprocess.run(argv, input=json.dumps(payload).encode(), cwd=cwd, env={**os.environ, "KCAP_URL": url},
                              stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, timeout=120)
        if done.returncode != 0:
            raise SystemExit(f"{event} hook exited {done.returncode}: {done.stderr.decode(errors='replace').strip()}")
    return time.perf_counter() - start


def event_count(url, session):
    try:
        with urllib.request.urlopen(f"{url}/api/sessions/{session}/summary", timeout=5) as r:
            return json.load(r).get("event_count")
    except Exception:
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("sizes", nargs="+", type=int)
    ap.add_argument("--url", default="http://localhost:9473")
    ap.add_argument("--wait", type=int, default=900)
    ap.add_argument("--kcap", default="kcap")
    a = ap.parse_args()

    commands = plugin_commands(a.kcap)
    runs = []
    for lines in a.sizes:
        session = str(uuid.uuid4())
        cwd = tempfile.mkdtemp(prefix="kcap-seed-")
        transcript = os.path.join(cwd, f"{session}.jsonl")
        write_transcript(transcript, session, cwd, lines)
        base = {"session_id": session, "transcript_path": transcript, "cwd": cwd}
        hook(commands, {**base, "hook_event_name": "SessionStart", "source": "resume"}, cwd, a.url)
        hook(commands, {**base, "hook_event_name": "UserPromptSubmit", "prompt": "go"}, cwd, a.url)
        hook(commands, {**base, "hook_event_name": "Stop"}, cwd, a.url)
        end_s = hook(commands, {**base, "hook_event_name": "SessionEnd", "reason": "exit"}, cwd, a.url)
        runs.append({"session": session.replace("-", ""), "lines": lines, "session_end_hook_s": round(end_s, 2), "start": time.time()})

    deadline = time.time() + a.wait
    pending = list(runs)
    while pending and time.time() < deadline:
        for run in list(pending):
            count = event_count(a.url, run["session"])
            run["event_count"] = count
            if count is not None and count >= run["lines"]:
                run["complete_after_s"] = round(time.time() - run["start"], 1)
                pending.remove(run)
        time.sleep(5)
    for run in runs:
        run.pop("start")
    print(json.dumps(runs, indent=2))


if __name__ == "__main__":
    main()
