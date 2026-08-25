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
apps/host/Campus.Service        background helper: the capture shortcut and a drop folder
apps/host/Campus.Indexer        reads documents in a process that can crash safely
apps/host/Campus.PluginHost     runs one extension, isolated from everything
apps/ios/CampusPocket           SwiftUI capture app
apps/ios/CampusShare            its share sheet extension
core/Campus.Domain              the object model
core/Campus.Vault               encryption and content-addressed storage
core/Campus.Storage             encrypted database, queries, search, change journal
core/Campus.Documents           what a file is, and how to read it
core/Campus.Sync                change bundles, pairing, and the phone protocol
core/Campus.Extensions.Sdk      what an extension declares and what it may do
core/Campus.Platform.Windows    Windows Hello, and registering with the shell
tools/brand                     the mark and wordmark, as geometry
tools/emoji                     the emoji catalogue generator
tools/dev/check-theme.py        fails the build if a colour is written outside the theme
docs/security.md                what the encryption does and does not protect
docs/emoji.md                   emoji artwork, and how to build an Apple pack
CHECKLIST.md                    every feature, and where it stands
```

## Installing

```powershell
pwsh tools/install/Install-Campus.ps1
```

That builds a release copy and puts it in `%LOCALAPPDATA%\Programs\Campus`, with a Start Menu
entry, a desktop shortcut and a line in Windows' Apps list. No administrator rights, nothing
written outside your own profile.

Run the same command again to update. It is safe to do that as often as you like, because the
program and the workspace are two different things in two different places:

| | |
|---|---|
| `%LOCALAPPDATA%\Programs\Campus` | the program — replaced wholesale on every update |
| `%LOCALAPPDATA%\Campus` | the vault, database, search index, settings — never touched |

An update is "delete the program, put a new one there", which is only safe because none of your
work is in there. The new build is published to a staging folder and only swapped in once it has
built successfully, so an update that fails leaves the working copy alone rather than half of it.

`-Uninstall` removes the program and leaves the workspace where it is; installing again finds it.
`-Uninstall -PurgeData` deletes the vault as well, and there is no copy of it anywhere else.

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

To build the thing you actually keep — one folder, no installer, no runtime to install first:

```bash
dotnet publish apps/desktop/Campus.Desktop/Campus.Desktop.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```

That folder is about 590 MB, and most of it is four copies of the .NET runtime: Campus and its
three helpers each carry their own, which is what makes the isolation real on a machine that has
never had .NET installed. `Campus.exe` is at the top; `service`, `indexer` and `pluginhost` sit
beside it, and `Assets/emoji-packs` holds the artwork if a pack has been built.

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

**Nothing that can crash runs in the app.** Document parsing happens in `Campus.Indexer`, and
extensions in `Campus.PluginHost`, each in a process of its own. The formats an import has to open
are the least trustworthy input Campus ever handles; one malformed PDF taking the session down
mid-import would be inexcusable. If a helper is missing, the same code runs in process — less
isolation, never a broken import.

**Annotations live beside the file, not inside it.** A highlight is stored as a rectangle in page
coordinates from zero to one, so it survives zooming, rotation, re-rendering and being opened on
another device. Writing into the PDF would change its bytes, and the bytes are its identity in the
vault — the same textbook on two machines would stop being the same object the moment one of them
marked a paragraph.

**Sync moves the change log, not the workspace.** Two devices exchange the entries neither has
seen, so syncing costs the size of what changed rather than the size of what exists. It travels as
a file you can carry or as the same bytes on a socket, encrypted with a key both sides derive from
a code typed once. Where both sides edited the same thing, Campus asks instead of guessing.

**A workspace nobody can leave is a trap.** Export writes a folder of markdown and the original
files — readable in anything, years from now, without this program — and says plainly that the
result is not encrypted. Backups are the opposite: a copy of the vault as it sits on disk, already
encrypted, openable only with the recovery key.

## Status

See [CHECKLIST.md](CHECKLIST.md). It is kept honest: `[x]` means built and running, `[~]` means
partly there, `[!]` means blocked on something real.

160 of 166 items are done. What is left is either a later idea (advanced PDF editing, presentation
authoring, spreadsheet editing, automation rules, an extension marketplace — which would need a
server Campus does not have) or an honest constraint: Campus does not drive Apple's AFC protocol
itself, so the phone's outbox is copied over a cable by hand or synced over Wi-Fi.
