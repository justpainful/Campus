"""
Builds Campus's emoji catalogue from the Unicode emoji test data.

Input is emoji-test.txt from unicode.org, which lists every emoji Unicode defines along with its
group, subgroup and name — including one entry per skin-tone variant. This script folds those
variants back onto the emoji they belong to, so the picker holds one entry per emoji with its
tones attached rather than five thousand unrelated squares.

Output is a tab-separated file rather than JSON: it parses in a single pass with no allocations
per field, and at roughly four thousand entries that difference is visible on the first keystroke
of a search.

Format:
    V<TAB>unicode-version
    G<TAB>group name
    S<TAB>subgroup name
    E<TAB>base code points<TAB>name<TAB>tone kind<TAB>variant code points separated by |
    A<TAB>base code points<TAB>alias alias alias

Tone kind: 0 none, 1 single (five variants), 2 dual (twenty-five combinations).

Run:  python tools/emoji/generate.py
      python tools/emoji/generate.py --source path/to/emoji-test.txt
"""

from __future__ import annotations

import argparse
import os
import re
import urllib.request

SOURCE_URL = "https://unicode.org/Public/emoji/latest/emoji-test.txt"
TARGET = "apps/desktop/Campus.Desktop/Assets/emoji.dat"

TONE_NAMES = ["light skin tone", "medium-light skin tone", "medium skin tone",
              "medium-dark skin tone", "dark skin tone"]
TONE_POINTS = ["1F3FB", "1F3FC", "1F3FD", "1F3FE", "1F3FF"]

# Words people actually type that the Unicode name does not contain. Kept short and deliberate;
# a thousand guessed synonyms would make search worse, not better.
ALIASES = {
    "1F44D": "ok yes agree approve",
    "1F44E": "no disagree reject",
    "1F602": "lol laugh crying funny",
    "1F923": "rofl lol laughing",
    "1F60D": "love adore crush",
    "2764 FE0F": "love heart red",
    "1F525": "fire lit hot",
    "1F389": "party celebrate congrats",
    "1F64F": "please thanks pray",
    "1F44F": "clap applause bravo",
    "1F914": "think hmm wondering",
    "1F622": "sad cry tears",
    "1F621": "angry mad furious",
    "1F634": "sleep tired bed",
    "2705": "done check tick complete",
    "274C": "no wrong cross error",
    "26A0 FE0F": "warning caution careful",
    "1F4DA": "books study school reading",
    "1F4DD": "note write homework memo",
    "270F FE0F": "pencil write edit",
    "1F393": "graduate school exam finish",
    "23F0": "alarm time deadline reminder",
    "1F4C5": "calendar date schedule",
    "1F4CC": "pin pinned important",
    "1F4A1": "idea light bulb tip",
    "1F680": "rocket launch ship fast",
    "1F3AF": "target goal aim focus",
    "1F4C8": "chart graph up progress",
    "1F9E0": "brain smart think memory",
    "1F4BB": "laptop computer work code",
    "1F4F1": "phone mobile iphone",
    "1F50D": "search find magnify look",
    "1F512": "lock secure private locked",
    "1F5D1 FE0F": "delete trash bin remove",
    "2B50": "star favourite favorite important",
    "1F4CE": "attach clip file attachment",
    "1F4E6": "package box archive",
    "1F6A8": "urgent alert emergency",
    "1F44B": "hi hello wave bye",
    "1F91D": "deal agree partnership handshake",
    "1F4AF": "hundred perfect score full marks",
}


def fetch(source: str | None) -> str:
    if source and os.path.exists(source):
        with open(source, encoding="utf-8") as f:
            return f.read()
    with urllib.request.urlopen(SOURCE_URL, timeout=60) as response:
        return response.read().decode("utf-8")


def strip_tones(name: str) -> tuple[str, list[str]]:
    """Splits 'waving hand: light skin tone' into its base name and the tones named."""
    if ":" not in name:
        return name, []
    base, _, detail = name.partition(":")
    tones = [t.strip() for t in detail.split(",")]
    if all(t in TONE_NAMES for t in tones):
        return base.strip(), tones
    return name, []


def parse(text: str):
    version = "unknown"
    group = subgroup = ""
    entries: dict[str, dict] = {}     # base code points -> entry
    by_name: dict[str, dict] = {}     # base name -> entry
    order: list[str] = []

    for raw in text.splitlines():
        line = raw.strip()

        if line.startswith("#"):
            if match := re.match(r"#\s*Version:\s*([\d.]+)", line):
                version = match.group(1)
            elif match := re.match(r"#\s*group:\s*(.+)", line):
                group = match.group(1).strip()
            elif match := re.match(r"#\s*subgroup:\s*(.+)", line):
                subgroup = match.group(1).strip()
            continue

        if not line:
            continue

        payload, _, comment = line.partition("#")
        code_part, _, status = payload.partition(";")
        status = status.strip()

        # Minimally-qualified and unqualified forms are the same emoji written a different way,
        # and components are the skin-tone modifiers themselves. Neither belongs in a picker.
        if status != "fully-qualified":
            continue

        codes = code_part.strip()
        name_match = re.match(r"\s*\S+\s+E[\d.]+\s+(.+)", comment)
        if not name_match:
            continue
        name = name_match.group(1).strip()

        base_name, tones = strip_tones(name)

        def add_base(base_codes: str, display_name: str) -> dict:
            entry = {
                "group": group,
                "subgroup": subgroup,
                "name": display_name,
                "base": base_codes,
                "variants": [],
                "tone_kind": 0,
            }
            entries[base_codes] = entry
            by_name[display_name] = entry
            order.append(base_codes)
            return entry

        if not tones:
            if base_name not in by_name:
                add_base(codes, base_name)
            continue

        # Variants are matched to their base by name, not by stripping modifiers out of the code
        # points: a toned "woman and man holding hands" is a ZWJ sequence whose untoned form is a
        # completely different code point, so code-point stripping would scatter twenty-five
        # combinations across twenty-five separate grid squares.
        parent = by_name.get(base_name)
        if parent is None:
            parent = add_base(codes, base_name)
            continue

        parent["variants"].append(codes)
        parent["tone_kind"] = max(parent["tone_kind"], 2 if len(tones) > 1 else 1)

    return version, [entries[c] for c in order]


def emit(version: str, entries: list[dict], target: str) -> None:
    os.makedirs(os.path.dirname(target), exist_ok=True)

    lines = [f"V\t{version}"]
    group = subgroup = None

    for entry in entries:
        if entry["group"] != group:
            group = entry["group"]
            subgroup = None
            lines.append(f"G\t{group}")
        if entry["subgroup"] != subgroup:
            subgroup = entry["subgroup"]
            lines.append(f"S\t{subgroup}")

        variants = "|".join(entry["variants"])
        lines.append(f"E\t{entry['base']}\t{entry['name']}\t{entry['tone_kind']}\t{variants}")

    for codes, aliases in ALIASES.items():
        lines.append(f"A\t{codes}\t{aliases}")

    with open(target, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
        f.write("\n")

    toned = sum(1 for e in entries if e["tone_kind"] > 0)
    variants = sum(len(e["variants"]) for e in entries)
    print(f"Unicode {version}")
    print(f"  {len(entries)} emoji, {toned} with skin tones, {variants} tone variants")
    print(f"  wrote {target} ({os.path.getsize(target) / 1024:.0f} KB)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", help="local emoji-test.txt instead of downloading")
    parser.add_argument("--out", default=TARGET)
    args = parser.parse_args()

    version, entries = parse(fetch(args.source))
    emit(version, entries, args.out)
