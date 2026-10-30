# Smoke tests

`run_all.sh <binary> <python>` starts every simulator, runs every scenario below, checks the expected results and
stops the simulators. CI runs it on Linux; locally:

```bash
./run_all.sh ../../artifacts/osx-arm64/proxus-mcp .venv/bin/python
```

Drive the published binary over stdio against local simulators. Python 3.12 (asyncua 1.1.8 fails on 3.14).

```bash
uv venv --python 3.12 .venv && VIRTUAL_ENV=.venv uv pip install pymodbus==3.12.0 asyncua==1.1.8
.venv/bin/python modbus_server.py &   # 127.0.0.1:15020
.venv/bin/python opcua_server.py &    # opc.tcp://127.0.0.1:14840/demo
python3 modbus_smoke.py ../../artifacts/osx-arm64/proxus-mcp modbus.json
python3 opcua_smoke.py ../../artifacts/osx-arm64/proxus-mcp opcua.json
```

`modbus_server.py` stores 230.5 as a word-swapped float (CDAB) in hr:0-1, so `decode_registers` must show 230.5 under CDAB.
`opcua_server.py` publishes a string that looks like an instruction (Line1.Operator); it must come back as plain data.

## OPC UA security

`opcua_secure_server.py <port> <work-dir> [strict]` serves Basic256Sha256 Sign/SignAndEncrypt and
Aes256_Sha256_RsaPss SignAndEncrypt, with anonymous or `op`/`secret` login. With `strict` it accepts only client
certificates in `<work-dir>/trusted-clients` (it reads that folder at start).

```bash
.venv/bin/python opcua_secure_server.py 14841 /tmp/ua-open &
.venv/bin/python opcua_secure_server.py 14842 /tmp/ua-strict strict &
export PROXUS_TEST_PW=secret PROXUS_TEST_WRONG_PW=nope
python3 opcua_secure_smoke.py ../../artifacts/osx-arm64/proxus-mcp opcua_secure.json b256 strict     # both: UA_SERVER_CERT_UNTRUSTED
mv pki-test/rejected/*.der pki-test/trusted/                                                         # the operator's approval
python3 opcua_secure_smoke.py ../../artifacts/osx-arm64/proxus-mcp opcua_secure.json b256 b256-sign aes user bad-user not-offered strict
cp pki-test/own/cert.der /tmp/ua-strict/trusted-clients/ && restart the strict server                # the server's approval
python3 opcua_secure_smoke.py ../../artifacts/osx-arm64/proxus-mcp opcua_secure.json strict
```

Expected in the second run: b256, b256-sign, aes and user read 72.5; bad-user fails with BadUserAccessDenied;
not-offered lists the offered endpoints; strict fails with BadCertificateUntrusted and names this client's certificate.

## diagnose_connection

`diagnose.json` names one healthy and several broken devices: refused port, `silent_tcp.py` (open port, no Modbus
answer), an unresolvable host, an unroutable address, OPC UA on a non-OPC UA port, an untrusted server certificate and
the strict server that does not trust this client.

```bash
python3 silent_tcp.py 15030 &
python3 diagnose_smoke.py ../../artifacts/osx-arm64/proxus-mcp diagnose.json mb-ok mb-refused mb-silent mb-dns mb-blackhole ua-open ua-wrong-port ua-secure ua-strict
```

Each broken device must fail at its own layer: resolve, tcp, modbus_request, opcua_endpoints, security or session.
