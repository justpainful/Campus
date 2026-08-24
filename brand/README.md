# Campus brand

Everything here is geometry, not artwork. The mark, the wordmark and every exported size come
from two Python files, so there is no bitmap to go stale and no font that has to be installed for
the name to render correctly.

## The mark

Three rounded `C` shapes, stepped down and to the right.

The two back layers are authored as **only their visible slivers** — a top bar and a left bar —
rather than as full shapes hidden behind the front one. That is what makes the gaps between
layers genuinely transparent: the mark is a single filled path with three non-overlapping
subpaths, so it sits on black, on white, and on a photograph without a background plate.

It carries no colour. Whatever foreground it is given is what it draws in.

```
tools/brand/logo.py          the mark
tools/brand/wordmark.py      the six letterforms
tools/brand/emit_xaml.py     writes Design/Brand.xaml from both
```

## The wordmark

Six constructed letterforms in a geometric sans: bowls are true circles, stems are straight,
terminals are round. `C`, `a`, `m`, `p` and `u` are built from arcs and lines; `s` is built from
cubic curves, because two tangent circles give a pinched waist that reads as a figure eight
rather than an ess.

Metrics, in design units:

| | |
|---|---|
| Cap height | 100 |
| x-height | 72 |
| Baseline | y = 100 |
| Descender | y = 128 |
| Stroke | 12.5 |
| Letter spacing | 21 |

## The lockup

Mark at 134 units against a 100-unit cap height, with a 34-unit gap. The wordmark's optical
centre sits on the mark's, not its baseline — matching baselines makes the mark look like it is
falling off the bottom.

## Clear space

A margin of 25% of the mark's height on every side. Nothing else enters it.

## Regenerating

```bash
python tools/brand/emit_xaml.py                       # in-app geometry
pwsh tools/brand/Export-BrandAssets.ps1               # every PNG size, both .ico files
pwsh tools/brand/Render-Preview.ps1                   # wordmark and lockup previews
```

The exporter rasterises the same path data the app draws, using WPF's geometry engine, so an
exported icon and the mark on screen cannot drift apart.

Output lands in `brand/generated/`, which is not committed — it is derived, and regenerating it
takes seconds.

| File | Use |
|---|---|
| `png/mark-*.png` | Transparent mark, in-app and for documents |
| `png/icon-rounded-*.png` | App icon, rounded plate |
| `png/icon-square-*.png` | App icon, square plate |
| `png/file-icon-*.png` | `.campus` document icon |
| `Campus.ico` | Multi-resolution app icon, compiled into the executable |
| `CampusFile.ico` | Multi-resolution document icon, for the shell association |

`.ico` frames are DIB rather than PNG. Modern Windows reads either, but the C# compiler's Win32
resource writer only reads DIB, and an icon the compiler rejects is not an icon.

## Colour

The mark has no colour of its own. In the app it takes `Theme.Label.Primary`, which is white in
dark mode and black in light mode. The exported icons use white on `#000000`, which is the
identity colour.

Never recolour the mark to an accent. Blue is for things you can press.
