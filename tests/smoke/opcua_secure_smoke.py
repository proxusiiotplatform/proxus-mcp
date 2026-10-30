"""Calls read_nodes on each device named on the command line and prints the outcome.
Usage: opcua_secure_smoke.py <binary> <config> <device> [<device> ...]"""
import json, subprocess, sys
p = subprocess.Popen([sys.argv[1], "--config", sys.argv[2]], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
def call(i, method, params):
    p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": i, "method": method, "params": params}) + "\n"); p.stdin.flush()
    while True:
        line = p.stdout.readline()
        if not line: raise SystemExit("closed: " + p.stderr.read())
        m = json.loads(line)
        if m.get("id") == i: return m
call(1, "initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "t", "version": "0"}})
p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n"); p.stdin.flush()
for i, device in enumerate(sys.argv[3:], start=2):
    r = call(i, "tools/call", {"name": "read_nodes", "arguments": {"device": device, "nodeIds": ["ns=2;s=Line1.Temperature"]}})["result"]
    print(f"== {device} isError={r['isError']}\n{r['content'][0]['text'][:700]}\n")
p.stdin.close(); p.wait(timeout=10)
