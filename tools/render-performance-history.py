#!/usr/bin/env python3
"""Render docs/assets/performance-history.svg from the checked-in benchmark data.

This renderer uses only the Python standard library so the chart can be rebuilt
without installing a plotting package.
"""

from __future__ import annotations

import json
from html import escape
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DATA_PATH = ROOT / "docs" / "benchmarks" / "performance-history.json"
OUTPUT_PATH = ROOT / "docs" / "benchmarks" / "performance-history.svg"

WIDTH = 1600
HEIGHT = 900
BG = "#0d1117"
PANEL = "#161b22"
GRID = "#30363d"
TEXT = "#f0f6fc"
MUTED = "#8b949e"
CYAN = "#58a6ff"
GREEN = "#3fb950"
ORANGE = "#d29922"


def text(x: float, y: float, value: str, size: int, *, fill: str = TEXT,
         weight: int = 400, anchor: str = "start") -> str:
    return (
        f'<text x="{x:.1f}" y="{y:.1f}" fill="{fill}" font-size="{size}" '
        f'font-weight="{weight}" text-anchor="{anchor}" '
        'font-family="Malgun Gothic, Apple SD Gothic Neo, Noto Sans KR, sans-serif">'
        f'{escape(value)}</text>'
    )


def line(x1: float, y1: float, x2: float, y2: float, *, stroke: str = GRID,
         width: float = 1, dash: str | None = None) -> str:
    dashed = f' stroke-dasharray="{dash}"' if dash else ""
    return (
        f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" '
        f'stroke="{stroke}" stroke-width="{width}"{dashed}/>'
    )


def render() -> str:
    data = json.loads(DATA_PATH.read_text(encoding="utf-8"))
    accuracy = data["pythonSecondAccuracy"]
    latency = data["csharpTestsPicLatency"]
    current = data["currentRelease"]

    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{WIDTH}" height="{HEIGHT}" viewBox="0 0 {WIDTH} {HEIGHT}" role="img" aria-labelledby="title desc">',
        '<title id="title">no-kalbak 버전별 OCR 성능 변화</title>',
        '<desc id="desc">Python 2차 버전 정확도 개선 곡선, C# 버전 인식 지연 개선, 1.0.1 현재 기준선</desc>',
        f'<rect width="{WIDTH}" height="{HEIGHT}" rx="24" fill="{BG}"/>',
        text(70, 70, "no-kalbak · OCR 성능 변화", 34, weight=700),
        text(70, 105, "기록에 남은 동일 평가 조건끼리만 선으로 연결", 18, fill=MUTED),
        f'<rect x="50" y="140" width="930" height="580" rx="18" fill="{PANEL}" stroke="{GRID}"/>',
        f'<rect x="1010" y="140" width="540" height="580" rx="18" fill="{PANEL}" stroke="{GRID}"/>',
        text(85, 190, "Python 2차 버전 · 전체 필드 통과율", 23, weight=700),
        text(85, 222, "153장 평가셋 · c 기준", 16, fill=MUTED),
    ]

    # Accuracy curve: fixed 0-100% axis, actual iteration numbers on x-axis.
    left, top, plot_w, plot_h = 120, 260, 810, 380
    for pct in range(0, 101, 20):
        y = top + plot_h - (pct / 100) * plot_h
        parts.extend([
            line(left, y, left + plot_w, y),
            text(left - 18, y + 6, f"{pct}%", 14, fill=MUTED, anchor="end"),
        ])
    for iteration in (0, 10, 20, 30, 40, 50, 60, 66):
        x = left + (iteration / 66) * plot_w
        parts.extend([
            line(x, top + plot_h, x, top + plot_h + 7, stroke=MUTED),
            text(x, top + plot_h + 28, str(iteration), 13, fill=MUTED, anchor="middle"),
        ])
    parts.append(text(left + plot_w / 2, 700, "튜닝 iteration", 15, fill=MUTED, anchor="middle"))

    points = accuracy["points"]
    coords = []
    for point in points:
        x = left + (point["iteration"] / 66) * plot_w
        y = top + plot_h - (point["successPercent"] / 100) * plot_h
        coords.append((x, y, point))
    path = " ".join(("M" if i == 0 else "L") + f" {x:.1f} {y:.1f}" for i, (x, y, _) in enumerate(coords))
    parts.append(f'<path d="{path}" fill="none" stroke="{CYAN}" stroke-width="5" stroke-linejoin="round" stroke-linecap="round"/>')
    for idx, (x, y, point) in enumerate(coords):
        prev = points[idx - 1]["successPercent"] if idx else point["successPercent"]
        color = ORANGE if point["successPercent"] < prev else CYAN
        parts.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="6" fill="{color}" stroke="{PANEL}" stroke-width="3"/>')

    # Label only important milestones so the curve stays readable.
    for idx in (0, 4, 9, 11, 13, 14, 15, 18, 19):
        x, y, point = coords[idx]
        label_y = y - 16 if idx != 15 else y + 28
        label_color = ORANGE if idx == 15 else TEXT
        parts.append(text(x, label_y, f'{point["successPercent"]:.2f}%', 14, fill=label_color, weight=700, anchor="middle"))
    final_x, final_y, _ = coords[-1]
    parts.append(f'<rect x="{final_x - 77:.1f}" y="{final_y - 66:.1f}" width="154" height="30" rx="15" fill="{CYAN}" opacity="0.16"/>')
    parts.append(text(final_x, final_y - 45, "147 / 153", 14, fill=CYAN, weight=700, anchor="middle"))

    # C# latency bars.
    parts.extend([
        text(1045, 190, "C# 버전 · 평균 인식 지연", 23, weight=700),
        text(1045, 222, "tests/pic 20장 · 낮을수록 좋음", 16, fill=MUTED),
    ])
    bar_left, bar_top, bar_w = 1190, 270, 300
    max_ms = 900
    for idx, point in enumerate(latency["points"]):
        y = bar_top + idx * 70
        width = (point["meanMs"] / max_ms) * bar_w
        bar_color = GREEN if idx == len(latency["points"]) - 1 else CYAN
        parts.extend([
            text(bar_left - 18, y + 21, point["label"], 15, fill=TEXT, anchor="end"),
            f'<rect x="{bar_left}" y="{y}" width="{bar_w}" height="28" rx="7" fill="#21262d"/>',
            f'<rect x="{bar_left}" y="{y}" width="{width:.1f}" height="28" rx="7" fill="{bar_color}" opacity="0.88"/>',
            text(bar_left + width - 10 if width > 80 else bar_left + width + 10, y + 20,
                 f'{point["meanMs"]} ms', 14,
                 fill=BG if width > 80 else TEXT, weight=700,
                 anchor="end" if width > 80 else "start"),
        ])
    parts.extend([
        text(1045, 637, "853 → 315 ms", 29, fill=GREEN, weight=700),
        text(1045, 670, "최종 기록 기준 63.1% 단축", 16, fill=MUTED),
        text(70, 770, "1.0.1 현재 기준선", 21, weight=700),
        text(70, 807, "핵심 필드", 15, fill=MUTED),
        text(170, 807, f'{current["coreExact"]["correct"]}/{current["coreExact"]["total"]}  ·  100%', 22, fill=GREEN, weight=700),
        text(440, 807, "능력치 값", 15, fill=MUTED),
        text(540, 807, f'{current["statValues"]["correct"]}/{current["statValues"]["total"]}  ·  100%', 22, fill=GREEN, weight=700),
        text(830, 807, "평균", 15, fill=MUTED),
        text(890, 807, f'{current["latencyMs"]["mean"]:.0f} ms', 22, fill=TEXT, weight=700),
        text(1085, 807, "P95", 15, fill=MUTED),
        text(1135, 807, f'{current["latencyMs"]["p95"]:.0f} ms', 22, fill=TEXT, weight=700),
        text(70, 850, "현재 기준선: 외부 143장 파일 입력. 실제 화면 캡처·커서 대기·WPF 표시 시간은 제외.", 14, fill=MUTED),
        text(1530, 850, "서로 다른 패널의 수치는 직접 비교하지 않음", 14, fill=MUTED, anchor="end"),
        "</svg>",
    ])
    return "\n".join(parts) + "\n"


def main() -> None:
    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT_PATH.write_text(render(), encoding="utf-8")
    print(f"wrote {OUTPUT_PATH.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
