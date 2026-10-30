import json, subprocess, sys
p = subprocess.Popen([sys.argv[1], "--config", sys.argv[2]], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
def call(i, method, params):
    p.stdin.write(json.dumps({"jsonrpc":"2.0","id":i,"method":method,"params":params})+"\n"); p.stdin.flush()
    while True:
        line = p.stdout.readline()
        if not line: raise SystemExit("closed: " + p.stderr.read())
        m = json.loads(line)
        if m.get("id") == i: return m
call(1,"initialize",{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}})
p.stdin.write(json.dumps({"jsonrpc":"2.0","method":"notifications/initialized"})+"\n"); p.stdin.flush()
tools = call(2,"tools/list",{})["result"]["tools"]
print("TOOLS", [t["name"] for t in tools])
print("READ_NODES SCHEMA", json.dumps([t for t in tools if t["name"]=="read_nodes"][0]["inputSchema"]))
def tool(i, name, args):
    r = call(i,"tools/call",{"name":name,"arguments":args})
    if "error" in r: print(f"\n== {name} PROTOCOL ERROR", r["error"]); return
    r = r["result"]; print(f"\n== {name} {args} isError={r.get('isError')}"); print(r["content"][0]["text"][:1100])
tool(3,"list_devices",{})
tool(4,"browse",{"device":"line1-ua"})
tool(5,"browse",{"device":"line1-ua","nodeId":"ns=2;s=Line1"})
tool(6,"read_nodes",{"device":"line1-ua","nodeIds":["ns=2;s=Line1.Temperature","ns=2;s=Line1.Running","ns=2;s=Line1.PartCount","ns=2;s=Line1.Operator","i=2258","ns=2;s=DoesNotExist"]})
tool(7,"read_nodes",{"device":"line1-ua","nodeIds":["nsu=urn:x;s=a"]})
tool(8,"read_address",{"device":"line1-ua","address":"hr:0"})
tool(9,"read_nodes",{"device":"meter-1","nodeIds":["i=2258"]})
tool(10,"read_nodes",{"device":"ua-locked","nodeIds":["i=2258"]})
tool(11,"watch_nodes",{"device":"line1-ua","nodeIds":["ns=2;s=Line1.Temperature","ns=2;s=DoesNotExist"],"durationSeconds":2,"samplingIntervalMs":250})
p.stdin.close(); p.wait(timeout=10); print("\nEXIT", p.returncode, p.stderr.read()[:400])
