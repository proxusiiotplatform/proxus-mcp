import json, subprocess, sys
p = subprocess.Popen([sys.argv[1], "--config", sys.argv[2]], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
def send(msg):
    p.stdin.write(json.dumps(msg) + "\n"); p.stdin.flush()
def call(i, method, params):
    send({"jsonrpc": "2.0", "id": i, "method": method, "params": params})
    while True:
        line = p.stdout.readline()
        if not line: raise SystemExit("server closed: " + p.stderr.read())
        m = json.loads(line)
        if m.get("id") == i: return m
r = call(1, "initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "m0-test", "version": "0"}})
print("INIT", r["result"]["serverInfo"], r["result"]["protocolVersion"])
send({"jsonrpc": "2.0", "method": "notifications/initialized"})
tools = call(2, "tools/list", {})["result"]["tools"]
for t in tools: print("TOOL", t["name"], t.get("annotations"), list(t["inputSchema"]["properties"]))
def tool(i, name, args):
    r = call(i, "tools/call", {"name": name, "arguments": args})["result"]
    print(f"\n== {name} {args} isError={r.get('isError')}")
    print(r["content"][0]["text"][:900])
tool(3, "list_devices", {})
tool(4, "read_address", {"device": "meter-1", "address": "hr:0", "count": 4})
tool(5, "read_address", {"device": "meter-1", "address": "coil:0", "count": 4})
tool(6, "read_address", {"device": "meter-1", "address": "30001", "count": 2})
tool(7, "decode_registers", {"device": "meter-1", "address": "hr:0", "count": 2})
tool(8, "read_address", {"device": "offline-plc", "address": "hr:0"})
tool(9, "read_address", {"device": "nope", "address": "hr:0"})
tool(10, "read_address", {"device": "meter-1", "address": "zz:1"})
tool(11, "read_address", {"device": "meter-1", "address": "hr:0", "count": 500})
tool(12, "list_devices", {})
tool(13, "watch_address", {"device": "meter-1", "address": "hr:0", "type": "float32", "byteOrder": "CDAB", "durationSeconds": 2, "intervalMs": 500})
p.stdin.close(); p.wait(timeout=10); print("\nEXIT", p.returncode); print("STDERR:", p.stderr.read()[:500])
