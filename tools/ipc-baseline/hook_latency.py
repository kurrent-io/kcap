#!/usr/bin/env python3
"""Wall time of the Claude hook processes one agent event starts, read from the plugin manifest.

Usage: hook_latency.py [--manifest PATH] [--runs N] [--cwd DIR] [--kcap PATH]
For each event (an Edit, a Bash, a Read, a prompt, a turn end) it runs every matching hook from the manifest with a
synthetic Claude payload, N times, and prints the process count, the synchronous count and the p50 of the summed
synchronous wall time. The payloads name a throwaway session id, so the server sees hook traffic for a session that
never started; point it at a local server you do not mind writing to."""
import argparse
import json
import os
import shlex
import statistics
import subprocess
import time
import uuid

EVENTS = {
    "edit":   ("PostToolUse", "Edit", {"tool_name": "Edit", "tool_input": {"file_path": "README.md", "old_string": "a", "new_string": "b"}, "tool_response": {}}),
    "bash":   ("PostToolUse", "Bash", {"tool_name": "Bash", "tool_input": {"command": "ls"}, "tool_response": {"stdout": ""}}),
    "read":   ("PostToolUse", "Read", {"tool_name": "Read", "tool_input": {"file_path": "README.md"}, "tool_response": {}}),
    "prompt": ("UserPromptSubmit", None, {"prompt": "hello", "permission_mode": "default"}),
    "stop":   ("Stop", None, {"stop_hook_active": False}),
}


def manifest_path(explicit):
    if explicit:
        return explicit
    here = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(here, "..", "..", "kcap", "hooks", "hooks.json")


def groups(manifest, event, tool):
    for group in manifest["hooks"].get(event, []):
        matcher = group.get("matcher")
        if tool and matcher and matcher != "*" and tool not in matcher.split("|"):
            continue
        yield group


def matching(manifest, event, tool):
    found = [(event, h) for g in groups(manifest, event, tool) for h in g["hooks"] if "if" not in h]
    # PreToolUse fires ahead of the same tool too.
    if event == "PostToolUse":
        found.extend(("PreToolUse", h) for g in groups(manifest, "PreToolUse", tool) for h in g["hooks"] if "if" not in h)
    return found


def run(hook, payload, cwd, kcap):
    argv = shlex.split(hook["command"])
    if kcap and argv[0] == "kcap":
        argv[0] = kcap
    start = time.perf_counter()
    done = subprocess.run(argv, input=json.dumps(payload).encode(), cwd=cwd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=hook.get("timeout", 10) + 5)
    return time.perf_counter() - start, done.returncode


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest")
    ap.add_argument("--runs", type=int, default=10)
    ap.add_argument("--cwd", default=os.getcwd())
    ap.add_argument("--kcap")
    a = ap.parse_args()
    manifest = json.load(open(manifest_path(a.manifest)))
    session = uuid.uuid4().hex

    report = {}
    for label, (event, tool, extra) in EVENTS.items():
        hooks = matching(manifest, event, tool)
        sync = [(e, h) for e, h in hooks if not h.get("async")]
        totals, failed = [], 0
        for _ in range(a.runs):
            took = 0.0
            for e, h in sync:
                seconds, code = run(h, {"hook_event_name": e, "session_id": session, "transcript_path": "/nonexistent.jsonl", "cwd": a.cwd, **extra}, a.cwd, a.kcap)
                took += seconds
                failed += code != 0
            totals.append(took)
        report[label] = {
            "processes": len(hooks),
            "synchronous": len(sync),
            "sync_sum_p50_ms": round(statistics.median(totals) * 1000, 1) if totals else 0,
            # A hook that fails fast looks fast: a non-zero count means the timing is not a baseline.
            "failed_runs": failed,
            "commands": [h["command"] for _, h in sync],
        }
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
