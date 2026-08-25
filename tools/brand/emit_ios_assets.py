#!/usr/bin/env python3
"""Writes the iOS asset catalogue from the generated brand assets.

The mark is defined once, as geometry, in `tools/brand/logo.py`. Everything that shows it — the
Windows icon, the in-app vector, the phone's home-screen icon — is derived from that definition
rather than drawn again, which is the only way three copies of a logo stay the same logo.

The catalogue is committed, unlike the rest of `brand/generated`, because the macOS runner that
builds the phone app cannot run the Windows exporter that produces it.

    pwsh tools/brand/Export-BrandAssets.ps1     # first, to produce the PNGs
    python tools/brand/emit_ios_assets.py
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "brand" / "generated" / "png" / "icon-square-1024.png"
CATALOGUE = ROOT / "apps" / "ios" / "CampusPocket" / "Assets.xcassets"


def write(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


def app_icon() -> None:
    """The home-screen icon: one 1024 square, which is all Xcode 14 and later needs."""
    if not SOURCE.exists():
        sys.exit(
            f"{SOURCE.relative_to(ROOT)} is missing. "
            "Run tools/brand/Export-BrandAssets.ps1 first."
        )

    icon_set = CATALOGUE / "AppIcon.appiconset"
    icon_set.mkdir(parents=True, exist_ok=True)

    # iOS refuses an icon with transparency, and the mark is drawn on an opaque plate anyway —
    # so the alpha channel is dropped rather than left to be argued about at submission time.
    image = Image.open(SOURCE).convert("RGB")
    image.save(icon_set / "icon-1024.png", format="PNG", optimize=True)

    write(icon_set / "Contents.json", {
        "images": [{"filename": "icon-1024.png", "idiom": "universal", "platform": "ios", "size": "1024x1024"}],
        "info": {"author": "campus", "version": 1},
    })


def colour(name: str, dark: tuple[int, int, int], light: tuple[int, int, int]) -> None:
    """
    One named colour, in both appearances.

    Written as sRGB components rather than hex so the file says what it is at a glance, and
    matching the desktop's own palette — a launch screen that flashes white before a black app is
    the first thing anybody notices and the last thing anybody reports.
    """
    def entry(rgb: tuple[int, int, int], appearance: str | None) -> dict:
        item: dict = {
            "color": {
                "color-space": "srgb",
                "components": {
                    "alpha": "1.000",
                    "red": f"0x{rgb[0]:02X}",
                    "green": f"0x{rgb[1]:02X}",
                    "blue": f"0x{rgb[2]:02X}",
                },
            },
            "idiom": "universal",
        }

        if appearance:
            item["appearances"] = [{"appearance": "luminosity", "value": appearance}]

        return item

    write(CATALOGUE / f"{name}.colorset" / "Contents.json", {
        "colors": [entry(light, None), entry(dark, "dark")],
        "info": {"author": "campus", "version": 1},
    })


def main() -> int:
    write(CATALOGUE / "Contents.json", {"info": {"author": "campus", "version": 1}})

    app_icon()

    # The same values the desktop theme uses: true black in dark, true white in light.
    colour("LaunchBackground", dark=(0x00, 0x00, 0x00), light=(0xFF, 0xFF, 0xFF))
    colour("AccentColor", dark=(0x0A, 0x84, 0xFF), light=(0x00, 0x7A, 0xFF))

    print(f"Wrote {CATALOGUE.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
