"""Drive vibe-acp over ACP stdio the way the daemon does, logging every frame."""
import json, subprocess, sys, threading, time, queue, os

cwd = sys.argv[1]
prompt = sys.argv[2] if len(sys.argv) > 2 else "Reply with exactly the word: pong"
log = open(os.path.join(os.path.dirname(__file__), "frames.log"), "w", encoding="utf-8")

p = subprocess.Popen(["vibe-acp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                     cwd=cwd, env={**os.environ, "KCAP_SESSION_KIND": "hosted_agent"})
inbox = queue.Queue()

def reader():
    for line in p.stdout:
        line = line.decode("utf-8", "replace").strip()
        if not line:
            continue
        log.write("<- " + line + "\n"); log.flush()
        try:
            inbox.put(json.loads(line))
        except Exception:
            pass

def err_reader():
    for line in p.stderr:
        log.write("ERR " + line.decode("utf-8", "replace")); log.flush()

threading.Thread(target=reader, daemon=True).start()
threading.Thread(target=err_reader, daemon=True).start()
next_id = 0

def send(obj):
    s = json.dumps(obj)
    log.write("-> " + s + "\n"); log.flush()
    p.stdin.write((s + "\n").encode()); p.stdin.flush()

def request(method, params, timeout=180):
    global next_id
    next_id += 1
    rid = next_id
    send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params})
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            msg = inbox.get(timeout=1)
        except queue.Empty:
            continue
        if msg.get("id") == rid and "method" not in msg:
            return msg
        if msg.get("method") == "session/request_permission":
            opts = msg["params"].get("options", [])
            allow = next((o for o in opts if o.get("kind", "").startswith("allow_once")), opts[0] if opts else None)
            print("PERMISSION", json.dumps(msg["params"].get("toolCall"))[:300], "->", allow and allow.get("optionId"))
            send({"jsonrpc": "2.0", "id": msg["id"],
                  "result": {"outcome": {"outcome": "selected", "optionId": allow["optionId"]}}})
        elif msg.get("method") == "session/update":
            u = msg["params"]["update"]
            kind = u.get("sessionUpdate")
            if kind == "agent_message_chunk":
                print("AGENT:", u.get("content", {}).get("text", ""), end="", flush=True)
            else:
                print("\nUPDATE", kind, json.dumps(u)[:200])
        elif "method" in msg and "id" in msg:
            print("\nUNHANDLED REQUEST", msg["method"])
            send({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": -32601, "message": "not supported"}})
    raise TimeoutError(method)

init = request("initialize", {"protocolVersion": 1, "clientCapabilities": {"fs": {"readTextFile": False, "writeTextFile": False}, "terminal": False},
                              "clientInfo": {"name": "kcap-probe", "version": "0"}})
print("INITIALIZE", json.dumps(init)[:1500])
new = request("session/new", {"cwd": cwd, "mcpServers": []})
print("SESSION/NEW", json.dumps(new)[:3000])
if "error" in new:
    sys.exit(1)
sid = new["result"]["sessionId"]
if len(sys.argv) > 3:
    print("SET MODEL", json.dumps(request("session/set_config_option", {"sessionId": sid, "configId": "model", "value": sys.argv[3]}))[:600])
res = request("session/prompt", {"sessionId": sid, "prompt": [{"type": "text", "text": prompt}]}, timeout=300)
print("\nPROMPT RESULT", json.dumps(res)[:800])
p.stdin.close()
try:
    p.wait(timeout=15)
except subprocess.TimeoutExpired:
    p.kill()
print("EXIT", p.returncode)
