#!/usr/bin/env python3
"""Render the README performance chart from checked-in benchmark data."""

from __future__ import annotations

import base64
import json
from html import escape
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DATA_PATH = ROOT / "docs" / "benchmarks" / "performance-history.json"
FONT_PATH = ROOT / "docs" / "fonts" / "DNFForgedBlade-Bold.woff2"
OUTPUT_PATH = ROOT / "docs" / "benchmarks" / "performance-history.svg"

WIDTH, HEIGHT = 1600, 900
BG = "#0b0a0d"
SURFACE = "#1e1824"
FG = "#f0e7d3"
MUTED = "#a2937b"
GOLD = "#c89b3c"
PALE_GOLD = "#f3d27a"
RED = "#8e2430"
GRID = "#393039"


def svg_text(x: float, y: float, value: str, size: int, *, fill: str = FG,
             anchor: str = "start", opacity: float = 1.0) -> str:
    return (
        f'<text x="{x:.1f}" y="{y:.1f}" fill="{fill}" fill-opacity="{opacity}" '
        f'font-size="{size}" text-anchor="{anchor}" '
        'font-family="DNFForgedBlade, Malgun Gothic, sans-serif">'
        f'{escape(value)}</text>'
    )


def svg_line(x1: float, y1: float, x2: float, y2: float, *, stroke: str = GRID,
             width: float = 1, opacity: float = 1.0) -> str:
    return (
        f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" '
        f'stroke="{stroke}" stroke-width="{width}" stroke-opacity="{opacity}"/>'
    )


def corner_frame() -> list[str]:
    x1, y1, x2, y2, arm = 26, 26, WIDTH - 26, HEIGHT - 26, 38
    frame = [
        f'<rect x="{x1}" y="{y1}" width="{x2-x1}" height="{y2-y1}" fill="none" '
        f'stroke="{GOLD}" stroke-opacity="0.28"/>',
    ]
    for xa, xb in ((x1, x1 + arm), (x2, x2 - arm)):
        frame.append(svg_line(xa, y1, xb, y1, stroke=GOLD, width=3))
        frame.append(svg_line(xa, y2, xb, y2, stroke=GOLD, width=3))
    for ya, yb in ((y1, y1 + arm), (y2, y2 - arm)):
        frame.append(svg_line(x1, ya, x1, yb, stroke=GOLD, width=3))
        frame.append(svg_line(x2, ya, x2, yb, stroke=GOLD, width=3))
    return frame


def render() -> str:
    data = json.loads(DATA_PATH.read_text(encoding="utf-8"))
    accuracy = data["pythonSecondAccuracy"]["points"]
    latency = data["csharpTestsPicLatency"]["points"]
    font_data = base64.b64encode(FONT_PATH.read_bytes()).decode("ascii")

    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{WIDTH}" height="{HEIGHT}" viewBox="0 0 {WIDTH} {HEIGHT}" role="img" aria-labelledby="title desc">',
        '<title id="title">no-kalbak OCR 성능 변화 그래프</title>',
        '<desc id="desc">Python 2차 버전 전체 필드 통과율과 C# 버전 평균 인식 지연</desc>',
        '<defs>',
        '<style>',
        f'@font-face{{font-family:DNFForgedBlade;src:url(data:font/woff2;base64,{font_data}) format("woff2");font-weight:700;}}',
        '</style>',
        '<radialGradient id="topGlow" cx="50%" cy="0%" r="95%"><stop offset="0" stop-color="#f3d27a" stop-opacity="0.075"/><stop offset="0.55" stop-color="#0b0a0d" stop-opacity="0"/></radialGradient>',
        '<radialGradient id="edgeShade" cx="50%" cy="45%" r="75%"><stop offset="0.55" stop-color="#0b0a0d" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity="0.48"/></radialGradient>',
        '<filter id="grain"><feTurbulence type="fractalNoise" baseFrequency="0.75" numOctaves="2" stitchTiles="stitchTiles"/><feColorMatrix type="saturate" values="0"/></filter>',
        '</defs>',
        f'<rect width="{WIDTH}" height="{HEIGHT}" fill="{BG}"/>',
        f'<rect width="{WIDTH}" height="{HEIGHT}" fill="url(#topGlow)"/>',
        f'<rect width="{WIDTH}" height="{HEIGHT}" fill="url(#edgeShade)"/>',
        f'<rect width="{WIDTH}" height="{HEIGHT}" filter="url(#grain)" opacity="0.035"/>',
        *corner_frame(),
        f'<rect x="92" y="76" width="15" height="15" transform="rotate(45 99.5 83.5)" fill="{GOLD}"/>',
        svg_text(126, 93, "Python 2차  ·  전체 필드 통과율 (%)  ·  153장", 25),
        f'<rect x="872" y="76" width="15" height="15" transform="rotate(45 879.5 83.5)" fill="{RED}"/>',
        svg_text(906, 93, "C#  ·  평균 인식 지연 (ms)  ·  tests/pic 20장", 25),
        svg_line(800, 138, 800, 805, stroke=GOLD, opacity=0.20),
    ]

    left, top, plot_w, plot_h = 108, 180, 650, 535
    for pct in range(0, 101, 20):
        y = top + plot_h - (pct / 100) * plot_h
        parts.extend([
            svg_line(left, y, left + plot_w, y, opacity=0.60),
            svg_text(left - 18, y + 7, f"{pct}", 17, fill=MUTED, anchor="end"),
        ])
    for iteration in (0, 10, 20, 30, 40, 50, 60, 66):
        x = left + (iteration / 66) * plot_w
        parts.extend([
            svg_line(x, top + plot_h, x, top + plot_h + 8, stroke=MUTED, opacity=0.75),
            svg_text(x, top + plot_h + 34, str(iteration), 16, fill=MUTED, anchor="middle"),
        ])

    coords = []
    for point in accuracy:
        x = left + (point["iteration"] / 66) * plot_w
        y = top + plot_h - (point["successPercent"] / 100) * plot_h
        coords.append((x, y, point))
    path = " ".join(("M" if idx == 0 else "L") + f" {x:.1f} {y:.1f}" for idx, (x, y, _) in enumerate(coords))
    parts.append(f'<path d="{path}" fill="none" stroke="{GOLD}" stroke-width="6" stroke-linecap="round" stroke-linejoin="round"/>')
    for idx, (x, y, point) in enumerate(coords):
        previous = accuracy[idx - 1]["successPercent"] if idx else point["successPercent"]
        color = RED if point["successPercent"] < previous else GOLD
        parts.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="6.5" fill="{color}" stroke="{BG}" stroke-width="3"/>')

    labels = {
        0: (0, -18, "middle"),
        4: (0, -18, "middle"),
        7: (0, -18, "middle"),
        9: (0, -18, "middle"),
        11: (0, -18, "middle"),
        13: (-8, -20, "end"),
        14: (-14, -22, "end"),
        15: (0, 29, "middle"),
        18: (-8, 30, "end"),
    }
    for idx, (dx, dy, anchor) in labels.items():
        x, y, point = coords[idx]
        color = RED if idx == 15 else FG
        parts.append(svg_text(x + dx, y + dy, f'{point["successPercent"]:.2f}', 17, fill=color, anchor=anchor))
    final_x, final_y, final_point = coords[-1]
    parts.append(svg_text(final_x - 13, final_y - 22, f'{final_point["successPercent"]:.2f}  (147/153)', 19, fill=PALE_GOLD, anchor="end"))
    parts.append(svg_text(left + plot_w / 2, 790, "iteration", 17, fill=MUTED, anchor="middle"))

    bar_label_x, bar_left, bar_w = 1015, 1138, 350
    bar_top, max_ms = 190, 900
    for tick in (0, 300, 600, 900):
        x = bar_left + (tick / max_ms) * bar_w
        parts.extend([
            svg_line(x, bar_top - 12, x, 723, opacity=0.45),
            svg_text(x, 757, str(tick), 16, fill=MUTED, anchor="middle"),
        ])
    for idx, point in enumerate(latency):
        y = bar_top + idx * 105
        width = (point["meanMs"] / max_ms) * bar_w
        color = GOLD if idx == len(latency) - 1 else RED
        parts.extend([
            svg_text(bar_label_x, y + 26, point["label"], 21, anchor="end"),
            f'<rect x="{bar_left}" y="{y}" width="{bar_w}" height="35" fill="{SURFACE}"/>',
            f'<rect x="{bar_left}" y="{y}" width="{width:.1f}" height="35" fill="{color}"/>',
            svg_text(bar_left + width - 10 if width >= 100 else bar_left + width + 10,
                     y + 26, f'{point["meanMs"]}', 19,
                     fill=BG if idx == len(latency) - 1 else FG,
                     anchor="end" if width >= 100 else "start"),
        ])
    parts.append(svg_text(bar_left + bar_w / 2, 790, "ms", 17, fill=MUTED, anchor="middle"))
    parts.append("</svg>")
    return "\n".join(parts) + "\n"


def main() -> None:
    OUTPUT_PATH.write_text(render(), encoding="utf-8")
    print(f"wrote {OUTPUT_PATH.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
