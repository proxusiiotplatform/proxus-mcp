# Proxus Industrial MCP Server

Gives AI assistants such as Claude Desktop, Cursor and VS Code read-only access to industrial devices. Supports Modbus TCP and OPC UA (Basic256Sha256 or Aes256_Sha256_RsaPss with Sign or SignAndEncrypt, or unsecured; anonymous or user-name login).

## Tools

| Tool | What it does |
|---|---|
| `list_devices` | Configured devices, endpoint, ISA-95 path, connection state |
| `read_address` | Raw registers or bits: `hr:0`, `ir:10`, `coil:5`, `di:0` or `40001` |
| `decode_registers` | Up to 16 registers read every common way: int16/32, uint, float32 in ABCD/CDAB/BADC/DCBA, float64 in both word orders |
| `browse` | Children of an OPC UA node (default: Objects folder) |
| `read_nodes` | Up to 50 OPC UA variables with data type, quality, status code and source timestamp |
| `diagnose_connection` | Why a device can't be reached: name, TCP, protocol handshake, security and certificates, login, a test read, identity and clock, step by step with the next thing to try |

Nothing can write to a device.

## OPC UA security

Set `security_mode` (`sign_and_encrypt`, `sign` or `none`) and `security_policy` (`Basic256Sha256` or
`Aes256_Sha256_RsaPss`) per device. Certificates live in `pki_dir` (default `pki/` next to the configuration file),
in the layout most OPC UA tools use:

| Folder | Holds |
|---|---|
| `own/` | This server's application certificate, created on first use (private key readable only by you) |
| `trusted/` | Server certificates you approved |
| `rejected/` | Server certificates seen but not approved |

Trust is never automatic, in either direction:

1. On the first connection the server's certificate lands in `rejected/` and the tool answers with its SHA-256
   thumbprint. Confirm the thumbprint with the server's administrator, then move the file to `trusted/`.
2. If the server answers `BadCertificateUntrusted` or `BadSecurityChecksFailed`, it has not trusted this client yet.
   `proxus-mcp certificate --config <file>` prints this server's certificate file and thumbprint to trust on the server.

Passwords never go in the configuration file: `password_env` names the environment variable that holds them. Errors
carry the OPC UA StatusCode name, the specification's description and a suggested next step.

## Build

Needs the .NET 10 SDK. The protocol drivers come from the [Proxus.Drivers](https://github.com/proxusiiotplatform/proxus-drivers) NuGet packages (Apache-2.0).

```bash
dotnet publish src/Proxus.Mcp.Server -c Release -r osx-arm64 -o artifacts/osx-arm64
```

The result is one native file, `artifacts/osx-arm64/proxus-mcp` (about 10 MB).

## Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "proxus": {
      "command": "/absolute/path/to/proxus-mcp",
      "args": ["--config", "/absolute/path/to/proxus-mcp.json"]
    }
  }
}
```

Copy `proxus-mcp.example.json` as a starting configuration.

## Tests

- `dotnet test tests/Proxus.Mcp.Tests`: unit tests, no devices needed.
- `tests/smoke/run_all.sh`: the published binary against Modbus and OPC UA simulators (see `tests/smoke/README.md`).
- CI (`.github/workflows/ci.yml`) runs both and publishes Native AOT binaries for linux-x64, linux-arm64, win-x64 and
  osx-arm64.

## Licence

Apache-2.0, see `LICENSE`.
