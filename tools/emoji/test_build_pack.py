"""
Tests the pack builder against a synthetic `sbix` font.

Apple Color Emoji stores its artwork in `sbix` and shapes through AAT `morx`, which is a
different pair of formats from the `CBDT`/`GSUB` fonts everyone else ships. This test builds a
tiny `sbix` font by hand and runs the real extraction code over it, so the Apple path is known to
work before anyone goes to the trouble of fetching the font.

    python tools/emoji/test_build_pack.py
"""

from __future__ import annotations

import os
import struct
import sys
import tempfile
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from fontTools.fontBuilder import FontBuilder
from fontTools.ttLib.tables._s_b_i_x import table__s_b_i_x
from fontTools.ttLib.tables.sbixGlyph import Glyph as SbixGlyph
from fontTools.ttLib.tables.sbixStrike import Strike as SbixStrike
from fontTools.pens.ttGlyphPen import TTGlyphPen

from build_pack import EmojiFont, to_filename, png_size


def solid_png(size: int, rgb: tuple[int, int, int]) -> bytes:
    """A minimal valid PNG, so the test does not need an image library to make one."""
    raw = b"".join(
        b"\x00" + bytes(rgb) * size
        for _ in range(size)
    )

    def chunk(tag: bytes, payload: bytes) -> bytes:
        return (struct.pack(">I", len(payload)) + tag + payload
                + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))

    header = struct.pack(">IIBBBBB", size, size, 8, 2, 0, 0, 0)   # 8-bit truecolour
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", header)
            + chunk(b"IDAT", zlib.compress(raw))
            + chunk(b"IEND", b""))


def build_probe_font(path: str) -> None:
    """A two-glyph font with an sbix strike, including a `dupe` record."""
    glyph_order = [".notdef", "wave", "waveDark"]

    builder = FontBuilder(unitsPerEm=1000)
    builder.setupGlyphOrder(glyph_order)
    builder.setupCharacterMap({0x1F44B: "wave", 0x1F44C: "waveDark"})

    pen = TTGlyphPen(None)
    empty = pen.glyph()
    builder.setupGlyf({name: empty for name in glyph_order})

    builder.setupHorizontalMetrics({name: (1000, 0) for name in glyph_order})
    builder.setupHorizontalHeader(ascent=800, descent=-200)
    builder.setupNameTable({"familyName": "Probe Color Emoji", "styleName": "Regular"})
    builder.setupOS2()
    builder.setupPost()

    font = builder.font

    sbix = table__s_b_i_x()
    sbix.version = 1
    sbix.flags = 1
    sbix.numStrikes = 2
    sbix.strikes = {}

    for ppem in (32, 160):
        strike = SbixStrike()
        strike.ppem = ppem
        strike.resolution = 72
        strike.glyphs = {}

        strike.glyphs["wave"] = SbixGlyph(
            glyphName="wave", graphicType="png ",
            imageData=solid_png(ppem, (0xFF, 0xCC, 0x00)))

        # A `dupe` record points at another glyph rather than carrying its own bitmap. Apple's
        # font uses these heavily, and a builder that ignores them silently loses emoji.
        strike.glyphs["waveDark"] = SbixGlyph(
            glyphName="waveDark", graphicType="dupe", referenceGlyphName="wave")

        sbix.strikes[ppem] = strike

    font["sbix"] = sbix
    font.save(path)


def main() -> int:
    failures: list[str] = []

    def check(condition: bool, message: str) -> None:
        if condition:
            print(f"  ok    {message}")
        else:
            print(f"  FAIL  {message}")
            failures.append(message)

    with tempfile.TemporaryDirectory() as workspace:
        font_path = os.path.join(workspace, "probe.ttf")
        build_probe_font(font_path)

        font = EmojiFont(font_path)
        print(f"{font.description}  ({font.kind})")

        check(font.kind == "sbix", "sbix is detected in preference to nothing")

        glyph = font.glyph_for("\U0001F44B")
        check(glyph is not None, "a single code point shapes to one glyph")

        png = font.png_for(glyph) if glyph is not None else None
        check(png is not None and png[:8] == b"\x89PNG\r\n\x1a\n", "the bitmap comes out as a PNG")

        # The largest strike must win: upscaling a 32px bitmap to 128 looks like a mistake.
        check(png is not None and png_size(png) == (160, 160), "the largest strike is used")

        dupe_glyph = font.glyph_for("\U0001F44C")
        dupe_png = font.png_for(dupe_glyph) if dupe_glyph is not None else None
        check(dupe_png is not None, "a dupe record resolves to the bitmap it points at")
        check(dupe_png == png, "the dupe resolves to the same image")

        missing = font.glyph_for("\U0001F600")
        check(missing is None, "an emoji the font does not have reports as missing")

        check(to_filename("1F469 1F3FB 200D 1F91D 200D 1F468 1F3FE")
              == "1f469-1f3fb-200d-1f91d-200d-1f468-1f3fe.png",
              "sequences are named the way emoji packs name them")

        font.close()

    print()
    if failures:
        print(f"{len(failures)} check(s) failed")
        return 1

    print("All checks passed. The Apple sbix path works.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
