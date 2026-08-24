# Emoji artwork

Campus draws emoji from an **artwork pack** — a folder of PNGs named by code point — and never
from a font the operating system happens to ship. There is no fallback. If no pack is installed,
the picker says so and draws nothing, because one Segoe UI Emoji face appearing in the middle of
an otherwise consistent set is precisely what packs exist to prevent.

## Getting Apple's emoji

Campus does not include Apple Color Emoji and cannot. Apple licenses that font for use on
Apple-branded hardware; a Windows application that shipped a copy would be redistributing it
outside those terms. Copies circulate on the internet — Campus will not fetch one, and neither
should you from a source you do not trust with a file that Windows will load into every process.

The legitimate path is a device you own.

### From a Mac

The font is at:

```
/System/Library/Fonts/Apple Color Emoji.ttc
```

Copy it to your PC — a USB stick, AirDrop to a folder, whatever is convenient — then build the
pack:

```bash
python tools/emoji/build_pack.py --font "Apple Color Emoji.ttc" --name apple \
    --license-note "Apple Color Emoji, from my own Mac"
```

That writes `apps/desktop/Campus.Desktop/Assets/emoji-packs/apple/`. Campus finds it on the next
launch; pick it in **Settings → Emoji → Artwork**.

To make emoji look right in plain text fields as well — the note editor, a search box — install
the same font into Windows (right-click → Install). Campus already lists `Apple Color Emoji`
ahead of `Segoe UI Emoji` in its font stack, so text fields follow automatically.

### From an iPhone

Not directly. On a device that has not been jailbroken, `/System/Library/Fonts/` is not reachable
by any app, by the Files app, or over USB — iOS does not expose the system font directory. There
is no supported route from an iPhone to this file.

If a Mac is not available to you, Campus stays as it is: no emoji artwork, and the picker says so
rather than substituting something.

## Building a pack from any colour font

The same command works for any bitmap colour emoji font:

```bash
python tools/emoji/build_pack.py --font <font> --name <pack-name>
```

Requirements: `pip install fonttools uharfbuzz`.

How it works, and why it is built this way:

- **HarfBuzz does the shaping.** Each of the 3,944 sequences in the catalogue is shaped, and the
  glyph it produces is the one extracted. That is what makes ZWJ sequences and skin tones come
  out as a single picture rather than four separate ones — and using a shaper rather than reading
  the substitution tables by hand is what lets the same code handle Apple, which shapes through
  AAT `morx`, and everyone else, who ships OpenType `GSUB`.
- **`sbix` and `CBDT` are both read.** Apple uses the first, Google and Microsoft the second.
  `sbix` `dupe` records — entries that point at another glyph rather than carrying their own
  bitmap, which Apple's font uses heavily — are followed rather than skipped.
- **The largest strike wins.** Apple's font carries several sizes; downscaling the 160px one
  beats upscaling the 32px one.

`tools/emoji/test_build_pack.py` builds a synthetic `sbix` font and runs the real extraction over
it, so the Apple path is known to work before anyone goes to the trouble of fetching the font:

```bash
python tools/emoji/test_build_pack.py
```

## Where packs live

| | |
|---|---|
| Shipped with the app | `apps/desktop/Campus.Desktop/Assets/emoji-packs/` |
| Installed by you | `%LOCALAPPDATA%\Campus\emoji-packs\` |

The second survives updates and needs no administrator rights. **Settings → Emoji → Packs
folder** opens it. Drop a pack folder in and Campus finds it.

Packs are not committed to the repository — they are derived from a font, they are large, and in
Apple's case the font is not ours to commit.

## The catalogue

Separate from the artwork. `Assets/emoji.dat` holds every emoji Unicode defines with its tone
variants attached, generated from `unicode.org`:

```bash
python tools/emoji/generate.py
```

Unicode 17.0: 2,194 emoji, 306 of which take skin tones, 1,750 tone variants — including the full
5×5 grid for each of the eleven two-person emoji that have one.
