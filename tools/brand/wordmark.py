"""
Campus wordmark — the six letterforms, rebuilt as geometry.

The reference wordmark is a geometric sans: bowls are true circles, stems are straight, and
terminals are round. That construction is reproducible from first principles, so the wordmark
is built from circle and line centrelines and stroked at a fixed weight rather than traced from
a bitmap or left depending on whatever font happens to be installed.

Metrics, in design units:
    cap height   100      (C spans this)
    x-height      72
    baseline     y = 100  (y increases downward)
    descender    y = 128  (p)
    stroke        11      (centreline weight, round caps and joins)

Run:  python tools/brand/wordmark.py            # prints the path data
      python tools/brand/wordmark.py --emit     # writes brand/generated/wordmark.*
"""

from __future__ import annotations

import argparse
import math
import os

CAP = 100.0
X_HEIGHT = 72.0
BASELINE = 100.0
DESCENDER = 128.0
STROKE = 12.5

X_TOP = BASELINE - X_HEIGHT            # 28 — top of lowercase letters
X_MID = X_TOP + STROKE / 2             # 33.5 — centreline at the top of a stem
X_BOTTOM = BASELINE - STROKE / 2       # 94.5 — centreline at the baseline

# Arch and bowl radius for the lowercase letters, on the centreline.
LC_RADIUS = (X_HEIGHT - STROKE) / 2    # 30.5
ARCH_RADIUS = 25.5

LETTER_SPACING = 21.0
ASCENT_MARGIN = 14.0                   # space above cap height in the exported box


def n(v: float) -> str:
    return f"{round(v, 2):g}"


def polar(cx: float, cy: float, r: float, degrees: float) -> tuple[float, float]:
    a = math.radians(degrees)
    return cx + r * math.cos(a), cy + r * math.sin(a)


def glyph_C() -> tuple[str, float]:
    """A circle open on the right, with the terminals cut at 42 degrees above and below."""
    r = (CAP - STROKE) / 2             # 44.5
    cx, cy = r, CAP / 2
    start = polar(cx, cy, r, 42)       # lower terminal
    end = polar(cx, cy, r, -42)        # upper terminal
    # Screen space has y pointing down, so increasing angle — sweep 1 — travels from the lower
    # terminal round the bottom, up the left side and over the top. Sweep 0 would cut across
    # the right and leave a comma rather than a C.
    path = (f"M{n(start[0])} {n(start[1])} "
            f"A{n(r)} {n(r)} 0 1 1 {n(end[0])} {n(end[1])}")
    return path, cx + r


def glyph_a() -> tuple[str, float]:
    """A circular bowl with a straight stem down its right side."""
    r = LC_RADIUS
    cx, cy = r, BASELINE - r - STROKE / 2
    bowl = (f"M{n(cx + r)} {n(cy)} "
            f"A{n(r)} {n(r)} 0 1 1 {n(cx - r)} {n(cy)} "
            f"A{n(r)} {n(r)} 0 1 1 {n(cx + r)} {n(cy)} Z")
    stem = f"M{n(cx + r)} {n(X_MID)} L{n(cx + r)} {n(X_BOTTOM)}"
    return f"{bowl} {stem}", cx + r


def glyph_m() -> tuple[str, float]:
    """A stem and two arches, each arch a true half circle."""
    r = ARCH_RADIUS
    spring = X_MID + r                 # centre height of the arch circles
    stem = f"M0 {n(X_MID)} L0 {n(X_BOTTOM)}"
    arch1 = (f"M0 {n(spring)} A{n(r)} {n(r)} 0 0 1 {n(2 * r)} {n(spring)} "
             f"L{n(2 * r)} {n(X_BOTTOM)}")
    arch2 = (f"M{n(2 * r)} {n(spring)} A{n(r)} {n(r)} 0 0 1 {n(4 * r)} {n(spring)} "
             f"L{n(4 * r)} {n(X_BOTTOM)}")
    return f"{stem} {arch1} {arch2}", 4 * r


def glyph_p() -> tuple[str, float]:
    """A descending stem with a circular bowl hung off it."""
    r = LC_RADIUS
    cy = BASELINE - r - STROKE / 2
    stem = f"M0 {n(X_MID)} L0 {n(DESCENDER - STROKE / 2)}"
    bowl = (f"M0 {n(cy)} "
            f"A{n(r)} {n(r)} 0 1 1 {n(2 * r)} {n(cy)} "
            f"A{n(r)} {n(r)} 0 1 1 0 {n(cy)} Z")
    return f"{stem} {bowl}", 2 * r


def glyph_u() -> tuple[str, float]:
    """Two stems joined by a half circle, with the right stem running on to the baseline."""
    r = ARCH_RADIUS
    turn = X_BOTTOM - r
    left = (f"M0 {n(X_MID)} L0 {n(turn)} "
            f"A{n(r)} {n(r)} 0 0 0 {n(2 * r)} {n(turn)} "
            f"L{n(2 * r)} {n(X_MID)}")
    tail = f"M{n(2 * r)} {n(turn)} L{n(2 * r)} {n(X_BOTTOM)}"
    return f"{left} {tail}", 2 * r


def glyph_s() -> tuple[str, float]:
    """
    Built from cubic curves rather than two circles. Tangent circles give a pinched waist that
    reads as a figure eight; a real s has a diagonal spine, which is what the middle two curves
    describe.
    """
    width = 44.0
    right = width - 5.0
    left = 5.0
    mid = width / 2

    path = " ".join([
        f"M{n(right)} {n(X_MID + 11.5)}",
        # up and over the top
        f"C{n(right)} {n(X_MID + 4.5)} {n(mid + 10)} {n(X_MID)} {n(mid)} {n(X_MID)}",
        f"C{n(mid - 10)} {n(X_MID)} {n(left)} {n(X_MID + 4.5)} {n(left)} {n(X_MID + 11)}",
        # the diagonal spine, crossing the waist
        f"C{n(left)} {n(X_MID + 17.5)} {n(left + 5)} {n(X_MID + 21)} {n(mid)} {n(X_MID + 24.5)}",
        f"C{n(mid + 12)} {n(X_MID + 28)} {n(right)} {n(X_MID + 32)} {n(right)} {n(X_MID + 39)}",
        # down and around the bottom
        f"C{n(right)} {n(X_BOTTOM - 15)} {n(mid + 10)} {n(X_BOTTOM)} {n(mid)} {n(X_BOTTOM)}",
        f"C{n(mid - 10)} {n(X_BOTTOM)} {n(left + 0.5)} {n(X_BOTTOM - 4)} {n(left)} {n(X_BOTTOM - 11)}",
    ])
    return path, width


GLYPHS = [glyph_C, glyph_a, glyph_m, glyph_p, glyph_u, glyph_s]


def wordmark() -> tuple[list[str], float, float]:
    """Lays the glyphs out on the baseline. Returns the paths, total width and total height."""
    paths: list[str] = []
    cursor = 0.0
    for index, builder in enumerate(GLYPHS):
        path, advance = builder()
        paths.append(translate(path, cursor, 0.0))
        cursor += advance + (LETTER_SPACING if index < len(GLYPHS) - 1 else 0.0)
    return paths, cursor, DESCENDER


def translate(path: str, dx: float, dy: float) -> str:
    """
    Shifts a path by rewriting its coordinates. Arc parameters keep their radii and flags, so
    only the endpoint pair of each arc moves.
    """
    tokens = path.replace(",", " ").split()
    out: list[str] = []
    i = 0
    while i < len(tokens):
        token = tokens[i]
        command = token[0]
        if command in "MLml":
            out.append(command)
            x = float(token[1:]) if len(token) > 1 else float(tokens[i + 1])
            offset = 1 if len(token) > 1 else 2
            y = float(tokens[i + offset])
            out.append(f"{n(x + dx)} {n(y + dy)}")
            i += offset + 1
        elif command == "A":
            rx = float(token[1:]) if len(token) > 1 else float(tokens[i + 1])
            offset = 1 if len(token) > 1 else 2
            ry = float(tokens[i + offset])
            rot = tokens[i + offset + 1]
            large = tokens[i + offset + 2]
            sweep = tokens[i + offset + 3]
            x = float(tokens[i + offset + 4])
            y = float(tokens[i + offset + 5])
            out.append(f"A{n(rx)} {n(ry)} {rot} {large} {sweep} {n(x + dx)} {n(y + dy)}")
            i += offset + 6
        elif command == "C":
            coords = []
            if len(token) > 1:
                coords.append(float(token[1:]))
            i += 1
            while len(coords) < 6:
                coords.append(float(tokens[i]))
                i += 1
            shifted = [
                coords[0] + dx, coords[1] + dy,
                coords[2] + dx, coords[3] + dy,
                coords[4] + dx, coords[5] + dy,
            ]
            out.append("C" + " ".join(n(v) for v in shifted))
            continue
        elif command in "Zz":
            out.append("Z")
            i += 1
        else:
            out.append(token)
            i += 1
    return " ".join(out)


def svg(width_px: int = 900) -> str:
    paths, width, height = wordmark()
    pad = STROKE / 2 + 2
    box_w = width + 2 * pad
    box_h = height + pad + ASCENT_MARGIN
    scale = width_px / box_w
    body = "".join(
        f'<path d="{p}" fill="none" stroke="#FFFFFF" stroke-width="{STROKE}" '
        f'stroke-linecap="round" stroke-linejoin="round"/>'
        for p in paths
    )
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width_px}" '
        f'height="{round(box_h * scale)}" viewBox="0 0 {round(box_w, 2)} {round(box_h, 2)}">'
        f'<g transform="translate({round(pad, 2)}, {round(ASCENT_MARGIN, 2)})">{body}</g></svg>'
    )


def emit(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    paths, width, height = wordmark()
    with open(os.path.join(out_dir, "wordmark.paths"), "w", encoding="utf-8") as f:
        f.write("\n".join(paths))
    with open(os.path.join(out_dir, "wordmark.svg"), "w", encoding="utf-8") as f:
        f.write(svg())
    with open(os.path.join(out_dir, "wordmark.metrics"), "w", encoding="utf-8") as f:
        f.write(f"width={round(width, 2)}\nheight={round(height, 2)}\nstroke={STROKE}\n"
                f"ascentMargin={ASCENT_MARGIN}\n")
    print(f"wrote wordmark to {out_dir}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--emit", action="store_true")
    parser.add_argument("--out", default="brand/generated")
    args = parser.parse_args()

    if args.emit:
        emit(args.out)
    else:
        for path in wordmark()[0]:
            print(path)
