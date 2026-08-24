<div align="center">

# Campus

**Personal Academic Workspace**

Everything to do with school in one place, on your own machine, encrypted.

</div>

---

Campus is not a file manager with a school theme. It is a workspace where a book, a note, an
assignment, a link and a thing you have to remember to print are all the same kind of object, so
one query can ask for "everything in Chemistry due this week" without any of it having been filed
in the right folder first.

Nothing leaves the device. There is no account, no server and no cloud — the phone talks to the
PC directly, over USB or your own Wi-Fi.

## Where things are

```
apps/desktop/Campus.Desktop     WinUI 3 application
apps/ios/CampusPocket           SwiftUI capture app
core/Campus.Domain              the object model
core/Campus.Vault               encryption and content-addressed storage
core/Campus.Storage             encrypted database, queries, search, change journal
core/Campus.Platform.Windows    Windows Hello
tools/brand                     the mark and wordmark, as geometry
tools/emoji                     the emoji catalogue generator
docs/security.md                what the encryption does and does not protect
docs/emoji.md                   emoji artwork, and how to build an Apple pack
CHECKLIST.md                    every feature, and where it stands
```

## Building

Windows 11, .NET 10 SDK. No workloads to install — the Windows App SDK arrives through NuGet.

```bash
dotnet build Campus.slnx -c Release
dotnet test tests/Campus.Core.Tests/Campus.Core.Tests.csproj
```

To run it:

```bash
dotnet run --project apps/desktop/Campus.Desktop/Campus.Desktop.csproj
```

A throwaway workspace full of invented content — six subjects, a week of homework — in its own
directory, never touching the real one. The title bar says **SAMPLE DATA** the whole time it is
open, so it cannot be mistaken for yours:

```bash
dotnet run --project apps/desktop/Campus.Desktop/Campus.Desktop.csproj -- --dev-workspace
```

To be rid of it, delete `%LOCALAPPDATA%\Campus\DevWorkspace`. Your real workspace lives in
`%LOCALAPPDATA%\Campus\Vault` and is never touched by that flag.

The iOS app builds on a macOS runner in CI; locally it needs Xcode 16 and XcodeGen:

```bash
cd apps/ios && xcodegen generate && open CampusPocket.xcodeproj
```

## Regenerating what is generated

The brand assets and the emoji catalogue are derived, not committed by hand. CI regenerates both
on every build, which is what proves the generators still work.

```bash
python tools/brand/emit_xaml.py            # in-app geometry
pwsh tools/brand/Export-BrandAssets.ps1    # PNGs and .ico files
python tools/emoji/generate.py             # emoji catalogue from unicode.org
```

## A few decisions worth knowing about

**Everything is a query, not a folder.** A subject's books, "due this week", the print queue —
each is a `CampusQuery` compiled to SQL. That is why one textbook can be in its subject, in the
library and in an exam collection at once without three copies existing.

**The vault leaks nothing on disk.** File names are keyed hashes, not the SHA-256 of the content,
so a directory listing cannot confirm that you hold a particular file. The database is SQLCipher,
so titles, tags and note bodies are as protected as the files.

**Windows Hello is not a checkpoint.** The key-encryption key is derived from a signature only
the Hello-protected private key can produce. Skipping the prompt does not skip the key.

**The recovery key is the only way back.** Shown once, never stored. `docs/security.md` explains
what that costs and why the alternative is worse.

**Icons are geometry, never emoji.** Around 150 symbols on a 24-unit grid, stroked at render time
with six weights and per-size optical correction. Emoji exist in Campus, but as content you
insert — never as interface.

**Emoji come from a pack, not from a font.** Campus never renders emoji with the system emoji
font, and has no fallback to one. Build a pack from a colour font you own — see
[docs/emoji.md](docs/emoji.md) — and every emoji in the app comes from it.

**One accent.** Blue acts, red destroys, amber warns, green confirms. Campus overrides Windows's
own accent colour inside its own controls, so a system set to orange does not turn half the app
orange and leave the other half blue.

## Status

See [CHECKLIST.md](CHECKLIST.md). It is kept honest: `[x]` means built and running, `[~]` means
partly there, `[!]` means blocked on something real.
