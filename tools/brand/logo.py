"""
Campus brand geometry — the single source of truth for the mark.

The reference artwork is a layered isometric C: three rounded C shapes stepped down and to the
right, separated by thin gaps. Rebuilding it as geometry rather than tracing a bitmap means the
mark is exact at 16px and at 1024px, needs no colour of its own, and can be emitted as XAML,
SVG or raster from one definition.

The two back layers are authored as the visible slivers only — a top bar plus a left bar — so
the three subpaths never overlap. That keeps the mark a single filled path that works on any
background, with the gaps genuinely transparent rather than painted in a background colour.

Run:  python tools/brand/logo.py            # prints the path data
      python tools/brand/logo.py --emit     # writes brand/generated/*
"""

from __future__ import annotations

import argparse
import math
import os

GRID = 48.0

# Layer origins, stepped down-right. Gap is the transparent channel between layers.
LAYER_ORIGINS = [(4.0, 4.0), (10.0, 9.0), (16.0, 14.0)]
GAP = 1.6

W, H = 28.0, 30.0          # outer size of one C
R_BACK = 10.0              # outer corner radius on the back layers
R_FRONT = 9.0              # slightly tighter on the front layer so the mouth has room
MOUTH_TOP = 10.5           # relative to the front layer origin
MOUTH_BOTTOM = 19.5
MOUTH_LEFT = 9.5           # arm thickness
MOUTH_RADIUS = 4.0


def n(v: float) -> str:
    return f"{round(v, 2):g}"


def _corner_x_at_y(cx: float, cy: float, r: float, y: float) -> float:
    """X on the right half of a circle at a given y — where a horizontal cut meets a corner."""
    dy = cy - y
    return cx + math.sqrt(max(r * r - dy * dy, 0.0))


def _corner_y_at_x(cx: float, cy: float, r: float, x: float) -> float:
    """Y on the lower half of a circle at a given x — where a vertical cut meets a corner."""
    dx = cx - x
    return cy + math.sqrt(max(r * r - dx * dx, 0.0))


def back_layer(origin: tuple[float, float], next_origin: tuple[float, float]) -> str:
    """
    The visible part of a layer that sits behind another one: an L of the top arm and the left
    arm, stopping a gap short of wherever the next layer begins.
    """
    x0, y0 = origin
    x_cut = next_origin[0] - GAP
    y_cut = next_origin[1] - GAP
    r = R_BACK

    tr_cx, tr_cy = x0 + W - r, y0 + r        # top-right corner centre
    bl_cx, bl_cy = x0 + r, y0 + H - r        # bottom-left corner centre

    top_right_x = _corner_x_at_y(tr_cx, tr_cy, r, y_cut)
    bottom_left_y = _corner_y_at_x(bl_cx, bl_cy, r, x_cut)

    return " ".join([
        f"M{n(x0 + r)} {n(y0)}",                                   # after the top-left corner
        f"L{n(x0 + W - r)} {n(y0)}",                               # along the top
        f"A{n(r)} {n(r)} 0 0 1 {n(top_right_x)} {n(y_cut)}",       # into the top-right corner
        f"L{n(x_cut)} {n(y_cut)}",                                 # back along the inner edge
        f"L{n(x_cut)} {n(bottom_left_y)}",                         # down the inner edge of the arm
        f"A{n(r)} {n(r)} 0 0 1 {n(x0)} {n(bl_cy)}",                # round the bottom-left corner
        f"L{n(x0)} {n(y0 + r)}",                                   # up the outer edge
        f"A{n(r)} {n(r)} 0 0 1 {n(x0 + r)} {n(y0)}",               # close the top-left corner
        "Z",
    ])


def front_layer(origin: tuple[float, float]) -> str:
    """The complete C: a rounded square with a rounded rectangular mouth cut from its right side."""
    x0, y0 = origin
    r = R_FRONT
    right = x0 + W
    bottom = y0 + H
    m_top = y0 + MOUTH_TOP
    m_bottom = y0 + MOUTH_BOTTOM
    m_left = x0 + MOUTH_LEFT
    mr = MOUTH_RADIUS

    return " ".join([
        f"M{n(x0 + r)} {n(y0)}",
        f"L{n(right - r)} {n(y0)}",
        f"A{n(r)} {n(r)} 0 0 1 {n(right)} {n(y0 + r)}",            # top-right
        f"L{n(right)} {n(m_top)}",
        f"L{n(m_left + mr)} {n(m_top)}",                           # along the top of the mouth
        f"A{n(mr)} {n(mr)} 0 0 0 {n(m_left)} {n(m_top + mr)}",     # concave corner, so it turns back
        f"L{n(m_left)} {n(m_bottom - mr)}",
        f"A{n(mr)} {n(mr)} 0 0 0 {n(m_left + mr)} {n(m_bottom)}",
        f"L{n(right)} {n(m_bottom)}",
        f"L{n(right)} {n(bottom - r)}",
        f"A{n(r)} {n(r)} 0 0 1 {n(right - r)} {n(bottom)}",        # bottom-right
        f"L{n(x0 + r)} {n(bottom)}",
        f"A{n(r)} {n(r)} 0 0 1 {n(x0)} {n(bottom - r)}",           # bottom-left
        f"L{n(x0)} {n(y0 + r)}",
        f"A{n(r)} {n(r)} 0 0 1 {n(x0 + r)} {n(y0)}",               # top-left
        "Z",
    ])


def mark_path() -> str:
    """All three layers as one path. Back to front, so a partial render still reads correctly."""
    parts = [
        back_layer(LAYER_ORIGINS[0], LAYER_ORIGINS[1]),
        back_layer(LAYER_ORIGINS[1], LAYER_ORIGINS[2]),
        front_layer(LAYER_ORIGINS[2]),
    ]
    return " ".join(parts)


def mark_layers() -> list[str]:
    """The layers separately, for animation and for the app icon's depth treatment."""
    return [
        back_layer(LAYER_ORIGINS[0], LAYER_ORIGINS[1]),
        back_layer(LAYER_ORIGINS[1], LAYER_ORIGINS[2]),
        front_layer(LAYER_ORIGINS[2]),
    ]


def svg(path: str, size: int = 512, foreground: str = "#FFFFFF",
        background: str | None = "#000000", radius: float | None = None) -> str:
    scale = size / GRID
    bg = ""
    if background is not None:
        if radius is None:
            bg = f'<rect width="{size}" height="{size}" fill="{background}"/>'
        else:
            bg = (f'<rect width="{size}" height="{size}" rx="{radius}" ry="{radius}" '
                  f'fill="{background}"/>')
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" '
        f'viewBox="0 0 {size} {size}">'
        f'{bg}'
        f'<g transform="scale({scale})">'
        f'<path d="{path}" fill="{foreground}" fill-rule="nonzero"/>'
        f'</g></svg>'
    )


def emit(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)

    path = mark_path()

    with open(os.path.join(out_dir, "mark.path"), "w", encoding="utf-8") as f:
        f.write(path)

    # Plain mark, transparent background — for use over any surface.
    with open(os.path.join(out_dir, "mark.svg"), "w", encoding="utf-8") as f:
        f.write(svg(path, 512, "#FFFFFF", None))

    # App icon, square and rounded.
    with open(os.path.join(out_dir, "icon-square.svg"), "w", encoding="utf-8") as f:
        f.write(svg(path, 1024, "#FFFFFF", "#000000"))
    with open(os.path.join(out_dir, "icon-rounded.svg"), "w", encoding="utf-8") as f:
        f.write(svg(path, 1024, "#FFFFFF", "#000000", radius=224))

    print(f"wrote brand geometry to {out_dir}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--emit", action="store_true", help="write files under brand/generated")
    parser.add_argument("--out", default="brand/generated")
    args = parser.parse_args()

    if args.emit:
        emit(args.out)
    else:
        for index, layer in enumerate(mark_layers(), start=1):
            print(f"layer {index}: {layer}\n")
        print("combined:")
        print(mark_path())
