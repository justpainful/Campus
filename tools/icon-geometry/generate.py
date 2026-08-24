"""
Generates the icon geometry that is easier to compute than to draw by hand — gears, stars,
radial arrangements. Everything is emitted on the same 24x24 grid the hand-authored icons use.

Run:  python tools/icon-geometry/generate.py
"""

import math

CENTER = 12.0
PRECISION = 2


def r(v: float) -> str:
    return f"{round(v, PRECISION):g}"


def polar(radius: float, degrees: float) -> tuple[float, float]:
    a = math.radians(degrees)
    return CENTER + radius * math.cos(a), CENTER + radius * math.sin(a)


def arc(radius: float, to_deg: float, sweep: int = 1) -> str:
    x, y = polar(radius, to_deg)
    return f"A{r(radius)} {r(radius)} 0 0 {sweep} {r(x)} {r(y)}"


def gear(teeth: int = 8, outer: float = 9.7, root: float = 7.5,
         tooth_outer_half: float = 10.0, tooth_root_half: float = 15.0,
         hole: float = 3.4) -> str:
    """Gear outline plus the centre hole, as one path with two subpaths."""
    step = 360.0 / teeth
    parts: list[str] = []

    start_x, start_y = polar(root, -step / 2 + tooth_root_half)
    parts.append(f"M{r(start_x)} {r(start_y)}")

    for i in range(teeth):
        a = i * step
        # rise from the root circle to the tooth tip
        x, y = polar(outer, a - tooth_outer_half)
        parts.append(f"L{r(x)} {r(y)}")
        # across the tip
        parts.append(arc(outer, a + tooth_outer_half))
        # fall back to the root circle
        x, y = polar(root, a + tooth_root_half)
        parts.append(f"L{r(x)} {r(y)}")
        # along the root circle to the next tooth
        parts.append(arc(root, a + step - tooth_root_half))

    parts.append("Z")

    # Centre hole, wound the other way so the even-odd fill leaves it open.
    hx, hy = polar(hole, 0)
    parts.append(f"M{r(hx)} {r(hy)}")
    parts.append(arc(hole, 180, sweep=0))
    parts.append(arc(hole, 360, sweep=0))
    parts.append("Z")

    return " ".join(parts)


def star(points: int = 5, outer: float = 9.0, inner_ratio: float = 0.48,
         rotation: float = -90.0) -> str:
    inner = outer * inner_ratio
    step = 360.0 / points
    parts: list[str] = []
    for i in range(points):
        ox, oy = polar(outer, rotation + i * step)
        ix, iy = polar(inner, rotation + i * step + step / 2)
        parts.append(f"{'M' if i == 0 else 'L'}{r(ox)} {r(oy)}")
        parts.append(f"L{r(ix)} {r(iy)}")
    parts.append("Z")
    return " ".join(parts)


def sun(rays: int = 8, core: float = 4.2, ray_inner: float = 6.6, ray_outer: float = 9.6) -> str:
    parts = [f"M{r(CENTER + core)} {r(CENTER)}", arc(core, 180), arc(core, 360), "Z"]
    step = 360.0 / rays
    for i in range(rays):
        a = i * step
        x1, y1 = polar(ray_inner, a)
        x2, y2 = polar(ray_outer, a)
        parts.append(f"M{r(x1)} {r(y1)} L{r(x2)} {r(y2)}")
    return " ".join(parts)


def spinner(segments: int = 8, inner: float = 5.4, outer: float = 9.2) -> str:
    parts = []
    step = 360.0 / segments
    for i in range(segments):
        a = -90 + i * step
        x1, y1 = polar(inner, a)
        x2, y2 = polar(outer, a)
        parts.append(f"M{r(x1)} {r(y1)} L{r(x2)} {r(y2)}")
    return " ".join(parts)


if __name__ == "__main__":
    generated = {
        "gear": gear(),
        "star": star(),
        "sun": sun(),
        "spinner": spinner(),
    }
    for name, data in generated.items():
        print(f'    ["{name}"] = "{data}",')
