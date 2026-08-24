# Emoji artwork

Campus draws emoji from an **artwork pack** — a folder of PNGs named by code point — and never
from a font the operating system happens to ship. There is no fallback. If no pack is installed,
the picker says so and draws nothing, because one Segoe UI Emoji face appearing in the middle of
an otherwise consistent set is precisely what packs exist to prevent.

## Getting Apple's emoji

Campus does not include Apple Color Emoji and cannot: Apple licenses that font for use on
Apple-branded hardware, so a Windows application shipping a copy would be redistributing it
outside those terms. The artwork has to come from you.

Two routes work. Both end with the same command, because the extractor reads the font rather
than caring where it came from.

### From a Mac

The font is at `/System/Library/Fonts/Apple Color Emoji.ttc`. Copy it to your PC and build:

```bash
python tools/emoji/build_pack.py --font "Apple Color Emoji.ttc" --name apple
```

### From a repackaged Windows build

Projects such as **emoji-win** take Apple's font and convert it into a form Windows renders
natively — `sbix` becomes `CBDT`, and AAT shaping becomes OpenType `GSUB`. Those builds work with
the extractor unchanged:

```bash
python tools/emoji/build_pack.py     --font "AppleColorEmojiForWindows.ttf" --name apple     --license-note "Apple Color Emoji artwork, repackaged (emoji-win, iOS 18.4)"
```

Two things are worth knowing before you go this way.

**It is still Apple's artwork.** The repackaging changes the container, not the licence. Whether
that is a reasonable thing to do on your own machine is your call, not the app's.

**The font renames itself.** These builds report their family as `Segoe UI Emoji` so that Windows
substitutes them for the system emoji font everywhere. That is what makes them work in Notepad
and the browser — and it also means installing one silently replaces Segoe system-wide.
Building a pack avoids that entirely: Campus reads the file, extracts the images, and never loads
the font into any process.

### From an iPhone

Not directly. On a device that has not been jailbroken, `/System/Library/Fonts/` is not reachable
by any app, by the Files app, or over USB. Which is why the repackaged builds exist.

### After building

The pack lands in `apps/desktop/Campus.Desktop/Assets/emoji-packs/apple/`. Campus finds it on the
next launch; choose it under **Settings → Emoji → Artwork**.

### About coverage

A pack only has what its font had. A build from iOS 18.4 covers 3,781 of the 3,944 sequences in
the catalogue — the missing 163 are emoji Unicode 16 and 17 added after that release, such as the
fingerprint and the face with bags under the eyes. Campus leaves those out of the picker rather
than offering a square it cannot fill, and Settings reports the shortfall instead of quoting a
bare total.

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

Packs are not committed to the repository. They are derived from a font, they run to tens of
megabytes, and Apple's artwork is not ours to publish — which matters rather more given this
repository is public.

## The catalogue

Separate from the artwork. `Assets/emoji.dat` holds every emoji Unicode defines with its tone
variants attached, generated from `unicode.org`:

```bash
python tools/emoji/generate.py
```

Unicode 17.0: 2,194 emoji, 306 of which take skin tones, 1,750 tone variants — including the full
5×5 grid for each of the eleven two-person emoji that have one.
