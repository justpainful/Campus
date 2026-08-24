# Emoji artwork

## What Campus ships

The complete emoji **system**:

- Every emoji Unicode defines — 2,194 in Unicode 17.0, generated from `emoji-test.txt`
- Every skin tone, including the 25 combinations that two-person emoji actually have, not the 5
  a naive reading of the data gives you
- Search by name, category and a curated alias list
- Categories, recently used, frequently used, pinning and hand ordering
- Press-and-hold for skin tones, remembered per emoji as well as globally

Regenerate the catalogue with:

```bash
python tools/emoji/generate.py
```

## What Campus does not ship

**Apple's emoji artwork.** Apple Color Emoji is proprietary and licensed only for use on Apple
platforms; redistributing it inside a Windows application would be a licensing violation, not a
technical difficulty. The same is true of Microsoft's, Google's and Twitter's sets to varying
degrees — Noto Color Emoji is the notable exception, being open-licensed.

So Campus draws emoji with whatever colour emoji font the system provides. On Windows 11 that is
Segoe UI Emoji, which covers the same Unicode set with different drawings.

## Using a different artwork set

The picker never assumes a font. `Theme.Font.Emoji` in `Design/Metrics.xaml` is the only place
the face is named:

```xml
<FontFamily x:Key="Theme.Font.Emoji">Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji</FontFamily>
```

To use a different set:

1. **A font you are licensed to use.** Install it and put its family name first in that list.
   Everything else keeps working — the catalogue, tones, search and preferences are all keyed by
   code point, not by artwork.

2. **An image pack.** Replace the `TextBlock` in `EmojiPicker.xaml`'s cell template with an
   `Image` whose source is derived from the entry's `Key` (`"1F44B 1F3FB"` → `1f44b-1f3fb.png`).
   `EmojiEntry.Key` is already the code-point sequence in the form every pack names its files by.

If you own a Mac, the font on it is licensed for that Mac. It is not licensed for redistribution
to a Windows PC, which is the situation Campus would be in if it shipped a copy — so that path is
yours to take deliberately, on your own machine, and not something the app can do for you.

## Why the data file rather than a package

`Assets/emoji.dat` is tab-separated and parses in one pass. At around 4,000 rows with tone
variants attached, the difference between that and deserialising JSON is visible on the first
keystroke of a search, which is exactly when a picker must not stutter.
