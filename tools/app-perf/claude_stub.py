#!/usr/bin/env python3
"""Stands in for the `claude` CLI when a kcap daemon hosts it, for exercising kcap without a model.

It does what kcap sees of Claude Code and nothing else: runs the kcap plugin's hook commands with Claude-shaped
payloads, writes a Claude transcript under ~/.claude/projects (or $CLAUDE_CONFIG_DIR), and takes its prompt from
the PTY as a bracketed paste. On each prompt it plays one turn: a Bash call that asks for permission, raised
through the PermissionRequest hook and left waiting for the answer, and meanwhile KCAP_STUB_TURNS turns of
ordinary tool work (about seven transcript lines each), so the request ends up that far back in the session's
stream. The answer settles the call: a tool_result, PostToolUse and Stop on allow, an error result on deny.

Environment: KCAP_STUB_TURNS (default 60), KCAP_STUB_PACE seconds between lines (default 0.02),
KCAP_STUB_KCAP the kcap to run (default `kcap` on PATH), KCAP_STUB_LOG a file for hook exits and outputs
(default ~/claude-stub.log). Exit with Ctrl-C, Ctrl-D or /exit; SIGTERM and SIGHUP end the session too.

Not modelled: the TUI's dialogs (so the daemon's terminal-dialog detection sees none), subagents, statusline,
real tool execution, and any hook outside the turn above. `claude -p` (kcap's title and slash-command helper
runs, which kcap starts with KCAP_SKIP=1) answers without firing hooks."""
import json
import os
import queue
import re
import select
import signal
import subprocess
import sys
import termios
import threading
import time
import tty
import uuid

KCAP = os.environ.get("KCAP_STUB_KCAP", "kcap")
TURNS = int(os.environ.get("KCAP_STUB_TURNS", "60"))
PACE = float(os.environ.get("KCAP_STUB_PACE", "0.02"))
LOG = os.environ.get("KCAP_STUB_LOG") or os.path.expanduser("~/claude-stub.log")
# The kcap plugin's hook commands (kcap/hooks/hooks.json) for the events played here.
HOOKS = {
    "SessionStart": (["hook", "--claude", "--pass-notices-capable", "--no-update-check"], 9, False),
    "UserPromptSubmit": (["hook", "--claude", "--title-prompt", "--pass-notices", "--workspace-span", "--no-update-check"], 5, False),
    "PreToolUse": (["hook", "--claude", "--no-update-check"], 5, False),
    "PermissionRequest": (["hook", "--claude", "--no-update-check"], 86400, False),
    "Notification": (["hook", "--claude", "--no-update-check"], 5, False),
    "PostToolUse": (["hook", "--claude", "--no-update-check"], 5, True),
    "Stop": (["hook", "--claude", "--stop-spans", "--no-update-check"], 5, False),
    "SessionEnd": (["hook", "--claude", "--no-update-check"], 15, True),
}


def log(line):
    with open(LOG, "a") as f:
        f.write(f"{time.strftime('%H:%M:%S')} {line}\n")


# Key sequences typed outside a paste (arrows, function keys) are not prompt text. PARTIAL is one cut off by the
# end of a read, kept until the rest of it arrives.
KEYS = re.compile(rb"\x1b\[[0-9;?]*[A-Za-z~]")
PARTIAL = re.compile(rb"\x1b(\[[0-9;?]*)?\Z")


def out(text):
    os.write(sys.stdout.fileno(), text.replace("\n", "\r\n").encode())


VALUED = {"--session-id", "--resume", "-r", "--permission-mode", "--model", "--effort", "--mcp-config",
          "--system-prompt", "--append-system-prompt", "--disallowedTools", "--allowedTools", "--settings"}


def parse_args(argv):
    """The session id, whether it resumes, the permission mode, and a prompt given as the last positional arg."""
    sid, resumed, mode, prompt = None, False, "default", None
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--version":
            print("2.1.999 (Claude Code stub)")
            sys.exit(0)
        if a == "--":
            prompt = " ".join(argv[i + 1:]) or None
            break
        if a in VALUED and i + 1 < len(argv):
            if a in ("--session-id", "--resume", "-r"):
                sid, resumed = argv[i + 1], a != "--session-id"
            elif a == "--permission-mode":
                mode = argv[i + 1]
            i += 2
            continue
        if not a.startswith("-"):
            prompt = a
        i += 1
    return sid or str(uuid.uuid4()), resumed, mode, prompt


class Session:
    def __init__(self, sid, resumed, mode):
        self.sid, self.mode, self.cwd = sid, mode, os.getcwd()
        home = os.environ.get("CLAUDE_CONFIG_DIR") or os.path.expanduser("~/.claude")
        project = os.path.join(home, "projects", self.cwd.replace("/", "-").replace(".", "-"))
        os.makedirs(project, exist_ok=True)
        self.transcript = os.path.join(project, f"{sid}.jsonl")
        self.parent = None
        self.lock = threading.Lock()
        self.source = "resume" if resumed else "startup"
        self.calls = 0
        self.running = set()

    def base(self, event):
        return {"session_id": self.sid, "transcript_path": self.transcript, "cwd": self.cwd,
                "permission_mode": self.mode, "hook_event_name": event}

    def hook(self, event, extra=None, wait=True):
        args, timeout, is_async = HOOKS[event]
        payload = json.dumps({**self.base(event), **(extra or {})})
        proc = subprocess.Popen([KCAP, *args], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        proc.stdin.write(payload.encode())
        proc.stdin.close()
        proc.stdin = None  # communicate() must not flush the closed pipe
        self.running.add(proc)
        if is_async or not wait:
            if is_async:
                threading.Thread(target=self._reap, args=(event, proc, timeout), daemon=True).start()
            return proc
        return self._reap(event, proc, timeout)

    def _reap(self, event, proc, timeout):
        try:
            stdout, stderr = proc.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            proc.kill()
            stdout, stderr = proc.communicate()
        self.running.discard(proc)
        log(f"{event} exit={proc.returncode} out={stdout.decode(errors='replace').strip()[:400]} "
            f"err={stderr.decode(errors='replace').strip()[:400]}")
        return proc.returncode, stdout.decode(errors="replace")

    def line(self, kind, message, extra=None):
        with self.lock:
            uid = str(uuid.uuid4())
            rec = {"parentUuid": self.parent, "isSidechain": False, "userType": "external", "cwd": self.cwd,
                   "sessionId": self.sid, "version": "2.1.999", "gitBranch": "", "type": kind, "message": message,
                   "uuid": uid, "timestamp": time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime())}
            rec.update(extra or {})
            with open(self.transcript, "a") as f:
                f.write(json.dumps(rec) + "\n")
            self.parent = uid

    def assistant(self, content):
        self.line("assistant", {"id": f"msg_{uuid.uuid4().hex[:24]}", "type": "message", "role": "assistant",
                                "model": "claude-stub", "content": content, "stop_reason": None,
                                "usage": {"input_tokens": 10, "output_tokens": 10}})

    def tool_result(self, call, text, error=False):
        self.line("user", {"role": "user", "content": [{"type": "tool_result", "tool_use_id": call, "content": text,
                                                        "is_error": error}]})

    def call_id(self):
        self.calls += 1
        return f"toolu_stub{self.calls:05d}{uuid.uuid4().hex[:8]}"


def busy_work(s, turns):
    for t in range(turns):
        s.assistant([{"type": "text", "text": f"Step {t + 1} of {turns}: checking module {t % 17}."}])
        for name, args, result in (("Read", {"file_path": f"{s.cwd}/src/Module{t % 17}.cs"}, "public class Module { }"),
                                   ("Grep", {"pattern": f"Retry{t}", "path": s.cwd}, "No matches found"),
                                   ("Glob", {"pattern": f"**/*{t % 5}.cs"}, "src/a.cs\nsrc/b.cs")):
            call = s.call_id()
            s.assistant([{"type": "tool_use", "id": call, "name": name, "input": args}])
            time.sleep(PACE)
            s.tool_result(call, result)
            time.sleep(PACE)
        if (t + 1) % 10 == 0:
            out(f"  … {t + 1}/{turns} steps\n")


def play_turn(s, prompt):
    s.hook("UserPromptSubmit", {"prompt": prompt})
    s.line("user", {"role": "user", "content": prompt})
    out(f"\n⏺ Working on: {prompt[:60]}\n")
    s.assistant([{"type": "text", "text": "I'll clear the old build output first."}])
    call = s.call_id()
    tool_input = {"command": "rm -rf ./build", "description": "Remove old build output"}
    s.assistant([{"type": "tool_use", "id": call, "name": "Bash", "input": tool_input}])
    _, pre = s.hook("PreToolUse", {"tool_name": "Bash", "tool_input": tool_input, "tool_use_id": call})
    decision = None
    try:
        decision = json.loads(pre).get("hookSpecificOutput", {}).get("permissionDecision") if pre.strip() else None
    except ValueError:
        pass
    if decision not in ("allow", "deny"):
        s.hook("Notification", {"message": "Claude needs your permission to use Bash", "notification_type": "permission_prompt"})
        out("\n  Bash command: rm -rf ./build\n  Waiting for permission (answer it in kcap)…\n")
        request = s.hook("PermissionRequest", {
            "tool_name": "Bash", "tool_input": tool_input, "tool_use_id": call,
            "permission_suggestions": [{"type": "addRules", "rules": [{"toolName": "Bash", "ruleContent": "rm -rf ./build"}],
                                        "behavior": "allow", "destination": "localSettings"}]}, wait=False)
        worker = threading.Thread(target=busy_work, args=(s, TURNS), daemon=True)
        worker.start()
        _, answer = s._reap("PermissionRequest", request, HOOKS["PermissionRequest"][1])
        decision = "deny"
        try:
            decision = json.loads(answer)["hookSpecificOutput"]["decision"]["behavior"]
        except (ValueError, KeyError, TypeError):
            pass
        worker.join()
    if decision == "allow":
        s.tool_result(call, "")
        s.hook("PostToolUse", {"tool_name": "Bash", "tool_input": tool_input, "tool_use_id": call,
                               "tool_response": {"stdout": "", "stderr": "", "interrupted": False}})
        s.assistant([{"type": "text", "text": "Removed the old build output."}])
        out("  ⎿ Allowed: removed ./build\n")
    else:
        s.tool_result(call, "The user doesn't want to proceed with this tool use. The tool use was rejected.", error=True)
        s.assistant([{"type": "text", "text": "Understood, I left ./build in place."}])
        out("  ⎿ Denied: left ./build in place\n")
    s.hook("Stop", {"stop_hook_active": False})
    out("\n> ")


def print_mode(argv):
    """`claude -p`: kcap's own helper runs (titles, the slash-command probe). They run with KCAP_SKIP=1, so
    the hooks real Claude fires there record nothing, and none are fired here."""
    log(f"print mode: {' '.join(argv)[:200]}")
    data = sys.stdin.read() if not sys.stdin.isatty() else ""
    if "stream-json" in argv:
        for line in data.splitlines():
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            if msg.get("type") == "control_request":
                print(json.dumps({"type": "control_response", "response": {
                    "subtype": "success", "request_id": msg.get("request_id"), "response": {"commands": []}}}))
        return
    print("Stub session")


def main():
    if "-p" in sys.argv[1:] or "--print" in sys.argv[1:]:
        print_mode(sys.argv[1:])
        return
    if sys.argv[1:3] == ["auth", "status"]:
        print(json.dumps({"loggedIn": False}))
        return
    sid, resumed, mode, first_prompt = parse_args(sys.argv[1:])
    s = Session(sid, resumed, mode)
    fd = sys.stdin.fileno()
    saved = termios.tcgetattr(fd) if os.isatty(fd) else None
    ended = threading.Event()

    def end(reason):
        if ended.is_set():
            return
        ended.set()
        for waiting in list(s.running):
            waiting.kill()
        proc = s.hook("SessionEnd", {"reason": reason})
        try:
            proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            pass
        if saved:
            termios.tcsetattr(fd, termios.TCSADRAIN, saved)
        out("\x1b[?2004l")
        os._exit(0)

    signal.signal(signal.SIGTERM, lambda *_: end("other"))
    signal.signal(signal.SIGHUP, lambda *_: end("other"))
    out(f"✻ Claude Code stub — session {sid}\n  cwd: {s.cwd}\n")
    s.hook("SessionStart", {"source": s.source, "model": "claude-stub"})
    if saved:
        tty.setraw(fd)
    out("\x1b[?2004h\n> ")
    # Turns run one at a time off the input loop, so Ctrl-C and Ctrl-D still end the session while one waits on
    # its permission request: raw mode turns them into bytes rather than signals.
    turns = queue.Queue()

    def take_turns():
        while True:
            play_turn(s, turns.get())

    threading.Thread(target=take_turns, daemon=True).start()
    if first_prompt:
        turns.put(first_prompt)
    buf, pasting, prompt_parts = b"", False, []

    def typed(text):
        text = KEYS.sub(b"", text)
        prompt_parts.append(text)
        out(text.decode(errors="replace"))

    while True:
        r, _, _ = select.select([fd], [], [], 1.0)
        if not r:
            continue
        chunk = os.read(fd, 4096)
        if not chunk or b"\x03" in chunk or b"\x04" in chunk:
            end("prompt_input_exit")
        buf += chunk
        while buf:
            if pasting:
                i = buf.find(b"\x1b[201~")
                if i < 0:
                    break
                pasted, buf, pasting = buf[:i], buf[i + 6:], False
                prompt_parts.append(pasted)
                out(pasted.decode(errors="replace"))
                continue
            i = buf.find(b"\x1b[200~")
            j = buf.find(b"\r")
            if i >= 0 and (j < 0 or i < j):
                typed(buf[:i])
                buf, pasting = buf[i + 6:], True
                continue
            if j >= 0:
                typed(buf[:j])
                buf = buf[j + 1:]
                prompt = b"".join(prompt_parts).decode(errors="replace").strip()
                prompt_parts.clear()
                if prompt == "/exit":
                    end("prompt_input_exit")
                if prompt:
                    turns.put(prompt)
                continue
            buf = KEYS.sub(b"", buf)
            k = buf.find(b"\x1b")
            if k < 0:
                typed(buf)
                buf = b""
            elif k > 0:
                typed(buf[:k])
                buf = buf[k:]
            elif PARTIAL.match(buf):
                break
            else:
                buf = buf[1:]  # a bare Esc or an unknown sequence's lead byte

if __name__ == "__main__":
    main()
