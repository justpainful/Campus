#!/usr/bin/env python3
"""Fails the build when a colour is written anywhere but the theme.

Campus has one rule that keeps the interface coherent: a component names a role, never a value.
The moment one file writes `#3A3A3C` directly, that file stops following the theme — it looks
right in dark mode, wrong in light, and invisible in high contrast, and nobody notices until
somebody changes their Windows settings.

The rule is only enforceable if it is checked, so it is checked here and in CI.

    python tools/dev/check-theme.py
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

# The files that are allowed to name colours, because they are where colours are defined.
ALLOWED = {
    "apps/desktop/Campus.Desktop/Design/Theme.xaml",
    "apps/desktop/Campus.Desktop/Design/Brand.xaml",
}

# Directories that are not ours to police.
SKIPPED = {"bin", "obj", ".git", "node_modules", "Assets", "brand"}

SEARCHED = {".xaml", ".cs"}

# A hex colour in XAML or C#: #RGB, #RRGGBB, #AARRGGBB, or Color.FromArgb-style literals.
HEX = re.compile(r'"#[0-9A-Fa-f]{3,8}"|Color\.FromArgb\s*\(|Colors\.[A-Z]')

# Transparent is the absence of a colour rather than a colour, and no theme can define it
# differently. It is allowed everywhere.
ALWAYS_ALLOWED = ("Colors.Transparent",)

# Named colours that are deliberate and defensible, with the reason.
EXCEPTIONS = {
    # A sheet of paper is white in both themes, because that is what a page is. Tinting it with
    # the theme would mean rendering a document onto a surface its author never chose.
    "PdfPageView.cs": "Colors.White",
    "PdfViewer.cs": "Colors.White",
}


def offences() -> list[str]:
    found: list[str] = []

    for path in sorted(ROOT.rglob("*")):
        if path.is_dir() or path.suffix not in SEARCHED:
            continue
        if any(part in SKIPPED for part in path.parts):
            continue

        relative = path.relative_to(ROOT).as_posix()
        if relative in ALLOWED:
            continue

        try:
            text = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue

        for number, line in enumerate(text.splitlines(), start=1):
            match = HEX.search(line)
            if not match:
                continue

            if any(allowed in line for allowed in ALWAYS_ALLOWED):
                continue

            allowed = EXCEPTIONS.get(path.name)
            if allowed and allowed in line:
                continue

            # A comment explaining a colour is not a colour.
            stripped = line.strip()
            if stripped.startswith("//") or stripped.startswith("<!--"):
                continue

            found.append(f"{relative}:{number}: {stripped[:100]}")

    return found


def main() -> int:
    found = offences()

    if not found:
        print("No raw colours outside the theme.")
        return 0

    print("Colours are named as roles, never written as values. These write values:\n")
    for line in found:
        print("  " + line)

    print(
        "\nUse a token from Design/ThemeTokens.cs instead, or add the value to Theme.xaml "
        "if it is genuinely a new role."
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
