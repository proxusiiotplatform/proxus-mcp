#!/usr/bin/env python3
"""Bundle register maps into the server binary.

    python3 tools/bundle_maps.py <modbus-register-maps>/dist

Reads every device JSON written by `python3 tools/maps.py build` in the modbus-register-maps
repository and writes src/Proxus.Mcp.Server/maps/register-maps.json.gz, which the next build embeds.
"""
import gzip
import json
import sys
from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / "src" / "Proxus.Mcp.Server" / "maps" / "register-maps.json.gz"


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(__doc__)
        return 2
    devices = Path(argv[1]) / "devices"
    maps = [json.loads(p.read_text(encoding="utf-8")) for p in sorted(devices.glob("*/*.json"))]
    if not maps:
        print(f"no maps under {devices}")
        return 1
    OUT.parent.mkdir(parents=True, exist_ok=True)
    data = json.dumps(maps, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    # mtime=0 keeps the archive byte-identical between runs.
    OUT.write_bytes(gzip.compress(data, compresslevel=9, mtime=0))
    print(f"{OUT}: {len(maps)} maps, {sum(len(m['registers']) for m in maps)} registers, {OUT.stat().st_size} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
