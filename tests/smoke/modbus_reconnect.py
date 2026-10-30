import json, subprocess, sys, time, os, signal
srv = lambda: subprocess.Popen([sys.argv[3], sys.argv[4]], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
p = subprocess.Popen([sys.argv[1], "--config", sys.argv[2]], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
def call(i, method, params):
    p.stdin.write(json.dumps({"jsonrpc":"2.0","id":i,"method":method,"params":params})+"\n"); p.stdin.flush()
    while True:
        m = json.loads(p.stdout.readline())
        if m.get("id") == i: return m
call(1,"initialize",{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"0"}})
p.stdin.write(json.dumps({"jsonrpc":"2.0","method":"notifications/initialized"})+"\n"); p.stdin.flush()
def read(i):
    t=time.time(); r=call(i,"tools/call",{"name":"read_address","arguments":{"device":"meter-1","address":"hr:2"}})["result"]
    print(f"{i}: isError={r['isError']} {time.time()-t:.2f}s {r['content'][0]['text'][:140]}")
read(2)
os.system("pkill -f modbus_server.py"); time.sleep(1)
read(3); read(4)
s = srv(); time.sleep(2)
read(5); read(6)
s.terminate(); p.stdin.close(); p.wait(timeout=10)
