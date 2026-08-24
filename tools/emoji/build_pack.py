"""
Turns a colour emoji font into a Campus emoji pack.

Campus renders emoji as images, not as text in a system font, so that the emoji you see are the
emoji you chose rather than whatever the operating system happens to ship. This script produces
the images.

It works by shaping each sequence with HarfBuzz and then pulling that glyph's bitmap out of the
font. Shaping is what makes ZWJ sequences and skin tones come out as one picture instead of four
— and using HarfBuzz rather than reading the substitution tables by hand is what makes the same
code work for both OpenType fonts (Noto, Twemoji) and Apple's, which shapes through AAT `morx`
rather than `GSUB`.

Supported bitmap formats: `sbix` (Apple) and `CBDT`/`CBLC` (Google, Microsoft).

    python tools/emoji/build_pack.py --font NotoColorEmoji.ttf --name noto
    python tools/emoji/build_pack.py --font AppleColorEmoji.ttc --name apple --license-note "..."

Apple's font is not included and cannot be: it is licensed for Apple hardware only. If you own a
Mac or an iPhone, the copy on that device is yours to use, and pointing this script at it is your
decision to make about your own machine.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import struct
import sys

from fontTools.ttLib import TTFont, TTCollection
import uharfbuzz as hb

CATALOGUE = "apps/desktop/Campus.Desktop/Assets/emoji.dat"
PACKS_DIR = "apps/desktop/Campus.Desktop/Assets/emoji-packs"


# --------------------------------------------------------------------------- catalogue

def read_sequences(path: str) -> list[str]:
    """Every code-point sequence the picker can show, base emoji and tone variants alike."""
    sequences: list[str] = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if parts[0] != "E" or len(parts) < 4:
                continue
            sequences.append(parts[1])
            if len(parts) > 4 and parts[4]:
                sequences.extend(parts[4].split("|"))
    return sequences


def to_text(code_points: str) -> str:
    return "".join(chr(int(p, 16)) for p in code_points.split())


def to_filename(code_points: str) -> str:
    """`1F44B 1F3FB` becomes `1f44b-1f3fb.png`, the naming every emoji pack already uses."""
    return "-".join(p.lower() for p in code_points.split()) + ".png"


# ------------------------------------------------------------------------------ font

class EmojiFont:
    """One colour font, with whichever bitmap table it happens to use."""

    def __init__(self, path: str, index: int = 0):
        self.path = path
        with open(path, "rb") as f:
            self.data = f.read()

        if path.lower().endswith(".ttc"):
            collection = TTCollection(path, lazy=True)
            if index >= len(collection.fonts):
                raise SystemExit(
                    f"{path} holds {len(collection.fonts)} fonts; --font-index {index} is out of range.")
            self.tt = collection.fonts[index]
        else:
            self.tt = TTFont(path, lazy=True, fontNumber=index if index else -1)

        face = hb.Face(self.data, index)
        self.font = hb.Font(face)
        self.upem = face.upem

        self.kind = self._detect()
        self._sbix_strike = self._largest_sbix_strike() if self.kind == "sbix" else None

    def _detect(self) -> str:
        if "sbix" in self.tt:
            return "sbix"
        if "CBDT" in self.tt:
            return "cbdt"
        if "COLR" in self.tt:
            raise SystemExit(
                "This font stores emoji as vector layers (COLR/CPAL) rather than bitmaps. "
                "Campus's pack builder reads bitmap tables; use a bitmap colour font such as "
                "Noto Color Emoji or Apple Color Emoji.")
        raise SystemExit(f"{self.path} has no colour bitmap table (looked for sbix and CBDT).")

    def _largest_sbix_strike(self):
        strikes = self.tt["sbix"].strikes
        # Biggest strike available: downscaling later beats upscaling a small one.
        return strikes[max(strikes.keys())]

    @property
    def description(self) -> str:
        try:
            name = self.tt["name"].getDebugName(4) or self.tt["name"].getDebugName(1)
        except Exception:
            name = None
        return name or os.path.basename(self.path)

    def glyph_for(self, text: str) -> int | None:
        """
        Shapes the sequence and returns its glyph, or None when the font renders it as more than
        one glyph — which is how a font says it does not have that emoji.

        The default feature set is used deliberately. Everyone else ships OpenType `GSUB`, but
        Apple shapes through AAT `morx`, and HarfBuzz picks the right machinery on its own when
        it is not told which features to force.
        """
        buffer = hb.Buffer()
        buffer.add_str(text)
        buffer.guess_segment_properties()
        hb.shape(self.font, buffer)

        infos = buffer.glyph_infos
        if len(infos) != 1:
            return None

        glyph = infos[0].codepoint
        return glyph if glyph != 0 else None

    def close(self) -> None:
        """Releases the file handle. fontTools opens lazily and holds it until told otherwise."""
        try:
            self.tt.close()
        except Exception:
            pass

    def __enter__(self) -> "EmojiFont":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def png_for(self, glyph: int) -> bytes | None:
        if self.kind == "sbix":
            return self._sbix_png(glyph)
        return self._cbdt_png(glyph)

    def _sbix_png(self, glyph: int, depth: int = 0) -> bytes | None:
        name = self.tt.getGlyphOrder()[glyph]
        record = self._sbix_strike.glyphs.get(name)
        if record is None:
            return None

        # A `dupe` record carries no bitmap of its own, only the name of the glyph that has one.
        # Apple's font is full of them, and a builder that ignores them loses those emoji.
        if record.graphicType == "dupe":
            if depth > 4:
                return None      # a cycle in the font, rather than a chain
            target = record.referenceGlyphName
            if not target:
                return None
            order = self.tt.getGlyphOrder()
            if target not in order:
                return None
            return self._sbix_png(order.index(target), depth + 1)

        if record.graphicType != "png ":
            return None
        return record.imageData

    def _cbdt_png(self, glyph: int) -> bytes | None:
        name = self.tt.getGlyphOrder()[glyph]
        for strike in self.tt["CBDT"].strikeData:
            record = strike.get(name)
            if record is None:
                continue
            data = getattr(record, "imageData", None)
            if data and data[:8] == b"\x89PNG\r\n\x1a\n":
                return data
        return None


# ---------------------------------------------------------------------------- packing

def png_size(data: bytes) -> tuple[int, int]:
    # IHDR width and height sit at a fixed offset, so the whole image never has to be decoded.
    width, height = struct.unpack(">II", data[16:24])
    return width, height


def build(font_path: str, name: str, index: int, out_root: str, catalogue: str,
          license_note: str | None) -> None:
    sequences = read_sequences(catalogue)
    if not sequences:
        raise SystemExit(f"No sequences found in {catalogue}. Run tools/emoji/generate.py first.")

    font = EmojiFont(font_path, index)
    out_dir = os.path.join(out_root, name)
    os.makedirs(out_dir, exist_ok=True)

    written = 0
    missing: list[str] = []
    sizes: set[tuple[int, int]] = set()
    total_bytes = 0

    for codes in sequences:
        glyph = font.glyph_for(to_text(codes))
        png = font.png_for(glyph) if glyph is not None else None

        if png is None:
            missing.append(codes)
            continue

        with open(os.path.join(out_dir, to_filename(codes)), "wb") as f:
            f.write(png)

        sizes.add(png_size(png))
        total_bytes += len(png)
        written += 1

    manifest = {
        "name": name,
        "source": font.description,
        "format": font.kind,
        "count": written,
        "missing": len(missing),
        "sizes": sorted(f"{w}x{h}" for w, h in sizes),
        "license": license_note or "",
    }
    with open(os.path.join(out_dir, "pack.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")

    font.close()

    coverage = written / len(sequences) * 100
    print(f"{font.description}  ({font.kind})")
    print(f"  {written} of {len(sequences)} sequences  ({coverage:.1f}% coverage)")
    print(f"  {total_bytes / 1024 / 1024:.1f} MB in {out_dir}")
    if missing:
        preview = ", ".join(missing[:6])
        print(f"  {len(missing)} not in this font, for example: {preview}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--font", required=True, help="colour emoji font (.ttf or .ttc)")
    parser.add_argument("--name", required=True, help="pack name, e.g. apple or noto")
    parser.add_argument("--font-index", type=int, default=0, help="font within a .ttc collection")
    parser.add_argument("--out", default=PACKS_DIR)
    parser.add_argument("--catalogue", default=CATALOGUE)
    parser.add_argument("--license-note", default=None,
                        help="recorded in pack.json so the pack carries its own terms")
    args = parser.parse_args()

    if not os.path.exists(args.font):
        sys.exit(f"No such font: {args.font}")

    build(args.font, args.name, args.font_index, args.out, args.catalogue, args.license_note)
