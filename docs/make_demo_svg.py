#!/usr/bin/env python3
"""Renders docs/demo.svg, an animated chat that replays a real proxus-mcp session.

Usage: make_demo_svg.py <demo.json> <out.svg>

demo.json: {"question": str, "tools": [[name, detail], ...], "answer": [str, ...], "footer": str}
Take the content from an actual run (claude -p ... --output-format stream-json) so the picture shows what the server
really returned. Each element fades in after the previous one; the whole replay loops.
"""
import html
import json
import sys
import textwrap

WIDTH = 880
PAD = 28
FONT = "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif"
MONO = "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace"
CYCLE = 22.0  # seconds for one replay


def wrap(text, width):
    return textwrap.wrap(text, width=width) or [""]


def main():
    data = json.load(open(sys.argv[1]))
    items = []  # (kind, y, height, payload)
    y = 70

    q_lines = wrap(data["question"], 78)
    items.append(("user", y, 24 + 22 * len(q_lines), q_lines))
    y += 24 + 22 * len(q_lines) + 18

    for name, detail in data["tools"]:
        items.append(("tool", y, 34, (name, detail)))
        y += 42
    y += 8

    for paragraph in data["answer"]:
        lines = wrap(paragraph, 92)
        items.append(("answer", y, 22 * len(lines), lines))
        y += 22 * len(lines) + 10

    height = y + 54
    step = (CYCLE * 0.62) / len(items)
    css = [f"text{{font-family:{FONT};}} .mono{{font-family:{MONO};}}",
           ".bg{fill:#f6f8fa;stroke:#d0d7de;} .card{fill:#ffffff;stroke:#d0d7de;} .ink{fill:#1f2328;} .soft{fill:#57606a;}",
           ".user{fill:#ddf4ff;stroke:#54aeff;} .tool{fill:#f6f8fa;stroke:#d0d7de;} .ok{fill:#1a7f37;} .brand{fill:#0f5d4e;}",
           "@media (prefers-color-scheme: dark){.bg{fill:#0d1117;stroke:#30363d;} .card{fill:#161b22;stroke:#30363d;}"
           " .ink{fill:#e6edf3;} .soft{fill:#8d96a0;} .user{fill:#0c2d6b;stroke:#1f6feb;} .tool{fill:#161b22;stroke:#30363d;}"
           " .ok{fill:#3fb950;} .brand{fill:#3fb9a0;}}"]
    body = []
    for index, (kind, top, h, payload) in enumerate(items):
        start = (index * step + 0.6) / CYCLE * 100
        css.append(f"@keyframes k{index}{{0%,{start:.2f}%{{opacity:0;transform:translateY(6px)}}"
                   f"{start + 2.5:.2f}%,94%{{opacity:1;transform:none}}100%{{opacity:0}}}}"
                   f".a{index}{{opacity:0;animation:k{index} {CYCLE}s ease-out infinite;}}")
        group = [f'<g class="a{index}">']
        if kind == "user":
            group.append(f'<rect class="user" x="{WIDTH - PAD - 700}" y="{top}" width="700" height="{h}" rx="12"/>')
            for i, line in enumerate(payload):
                group.append(f'<text class="ink" x="{WIDTH - PAD - 684}" y="{top + 26 + 22 * i}" font-size="15">{html.escape(line)}</text>')
        elif kind == "tool":
            name, detail = payload
            group.append(f'<rect class="tool" x="{PAD + 8}" y="{top}" width="{WIDTH - 2 * PAD - 16}" height="{h}" rx="8"/>')
            group.append(f'<text class="ok" x="{PAD + 22}" y="{top + 22}" font-size="14">✓</text>')
            group.append(f'<text class="ink mono" x="{PAD + 42}" y="{top + 22}" font-size="13.5" font-weight="600">{html.escape(name)}</text>')
            group.append(f'<text class="soft mono" x="{PAD + 42 + 8.4 * len(name) + 14}" y="{top + 22}" font-size="13">{html.escape(detail)}</text>')
        else:
            for i, line in enumerate(payload):
                group.append(f'<text class="ink" x="{PAD + 8}" y="{top + 16 + 22 * i}" font-size="15">{html.escape(line)}</text>')
        group.append("</g>")
        body.append("".join(group))

    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" width="{WIDTH}" height="{height}" viewBox="0 0 {WIDTH} {height}" role="img" aria-label="{html.escape(data["question"])}">
<style>{"".join(css)}</style>
<rect class="bg" x="0.5" y="0.5" width="{WIDTH - 1}" height="{height - 1}" rx="14"/>
<circle cx="{PAD}" cy="32" r="6" fill="#ff5f57"/><circle cx="{PAD + 20}" cy="32" r="6" fill="#febc2e"/><circle cx="{PAD + 40}" cy="32" r="6" fill="#28c840"/>
<text class="soft" x="{PAD + 62}" y="37" font-size="14">AI assistant + <tspan class="brand" font-weight="700">Proxus Industrial MCP Server</tspan></text>
{"".join(body)}
<text class="soft" x="{PAD}" y="{height - 22}" font-size="13">{html.escape(data["footer"])}</text>
</svg>
'''
    open(sys.argv[2], "w").write(svg)
    print(f"{sys.argv[2]}: {len(items)} steps, {WIDTH}x{height}, {len(svg)} bytes")


if __name__ == "__main__":
    main()
