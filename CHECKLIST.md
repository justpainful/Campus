# Campus — Master Build Checklist

> Personal Academic Workspace · Windows Native (WinUI 3) + iOS (SwiftUI)
> Legend: `[ ]` not started · `[~]` in progress · `[x]` done & builds · `[!]` blocked / needs decision

---

## P0 — Foundation

- [x] F-01 Verify WinUI 3 toolchain builds on target machine (WindowsAppSDK 2.4.0 / .NET 10)
- [x] F-02 Solution layout (`Campus.sln`) + `Directory.Build.props` + `Directory.Packages.props`
- [x] F-03 `Campus.Domain` — object model, enums, value types
- [x] F-04 `Campus.Storage` — SQLite schema, migrations, repositories
- [x] F-05 `Campus.Vault` — crypto primitives, content-addressed object store
- [x] F-06 `Campus.Search` — FTS5 index + query parser
- [~] F-07 `Campus.Sync` — change journal, device registry, conflict engine
- [~] F-08 `Campus.Extensions.Sdk` — public extension contracts
- [x] F-09 DI container + app host bootstrap
- [~] F-10 Logging, crash capture, diagnostics
- [x] F-11 `.gitignore`, `.editorconfig`, repo hygiene
- [x] F-12 Unit test project + CI build for desktop

## P0 — Brand & Design System

- [x] B-01 Rebuild logo mark (layered isometric `C`) as pure vector geometry (XAML `PathGeometry` + SVG)
- [x] B-02 Rebuild wordmark "Campus" as vector geometry (no font dependency)
- [x] B-03 Full horizontal lockup (mark + wordmark) with correct clear-space rules
- [x] B-04 App icon — rounded variant, 1024/512/256/128/64/32/16
- [x] B-05 App icon — square variant, all sizes
- [x] B-06 `.campus` file-type icon, all sizes
- [x] B-07 Generate multi-resolution `.ico` + `.png` asset pack from geometry
- [x] B-08 Brand usage doc (`brand/README.md`)

### Theme (Semantic Color System)

- [x] T-01 `ColorSystem` token tree: Background / GroupedBackground / Surface / Label / Fill / Separator
- [x] T-02 Semantic state tokens: Accent, Destructive, Success, Warning, Info, Disabled, Selected, Focused
- [x] T-03 Dark palette (`#000000` / `#1C1C1E` / `#2C2C2E` / `#3A3A3C`) — neutral, no blue tint
- [x] T-04 Light palette (`#FFFFFF` / `#F2F2F7`)
- [x] T-05 Label hierarchy with true alpha ramps (1.0 / 0.6 / 0.3 / quaternary)
- [x] T-06 Fill tokens distinct from Surface tokens (hover / pressed / selection)
- [x] T-07 Separator Standard + Opaque
- [x] T-08 Accent ramp: Primary / Hover / Pressed / Disabled
- [x] T-09 `ThemeResolver` — central resolver (appearance + system + a11y + control state)
- [x] T-10 Appearance modes: System / Light / Dark, System = default
- [x] T-11 Live OS theme change with no restart
- [~] T-12 High-contrast / Increase-Contrast adaptation
- [~] T-13 Reduced-transparency adaptation
- [ ] T-14 Lint rule: no raw HEX outside theme definition files
- [x] T-15 Component role bindings (Button, SettingsRow, Page, Input, Toggle, List…)
- [x] T-16 `Page` kinds: Standard Content Page vs Grouped Page
- [x] T-17 Reusable `SettingsSection` component (header / grouped surface / rows / separators / footer)
- [x] T-18 Elevation & shadow policy (floating surfaces only)
- [x] T-19 Corner radius system, kept independent of color system
- [x] T-20 Theme Gallery / Preview page with System·Light·Dark toggle

### Iconography & Emoji

- [x] I-01 Icon engine: vector glyph set, weight + optical-size aware (SF-Symbols-like), never emoji
- [x] I-02 Full app icon inventory (~180 glyphs) drawn as path data
- [x] I-03 Icon sizing scale + comfortable hit targets (min 32px control, 44px touch)
- [x] I-04 Icon color always via `Label.*` / semantic tokens
- [x] E-01 Emoji data pipeline — full Unicode emoji set, groups, subgroups, CLDR names, keywords
- [x] E-02 Skin-tone variant model (all 5 Fitzpatrick modifiers + default, incl. multi-person sequences)
- [x] E-03 Emoji picker UI: categories, search, recents, frequently used
- [x] E-04 Press-and-hold → skin-tone flyout, per-emoji tone memory
- [x] E-05 Custom ordering / pinning / favorites
- [x] E-06 Emoji renderer abstraction (font-based today, asset-pack-swappable)
- [!] E-07 Apple emoji artwork — **licensing blocker**, see `docs/emoji-licensing.md`; picker ships asset-pack-ready

---

## P1 — Security & Vault

- [x] S-01 Windows Hello unlock (`UserConsentVerifier` + WinUI interop)
- [x] S-02 Key hierarchy: Hello-gated key protector → Master Key → per-object keys
- [x] S-03 AES-256-GCM object encryption
- [x] S-04 Encrypted filenames + metadata + thumbnails + index + database
- [x] S-05 Content-addressed storage (SHA-256, dedup)
- [x] S-06 Vault layout: `objects/` `chunks/` `thumbnails/` `index/`
- [x] S-07 Desktop shortcut + real vault under `%LOCALAPPDATA%\Campus`
- [x] S-08 Recovery Key generation + verification flow
- [x] S-09 Lock command (`Ctrl+Shift+L`) — zeroize keys, close viewers, clear buffers
- [x] S-10 Auto-lock policies (5/10/30 min, on PC lock, on app close)
- [ ] S-11 Export flow with optional re-authentication
- [ ] S-12 Sensitive Mode (drag-out restrictions, clipboard clearing)
- [x] S-13 Honest threat-model doc — what encryption does and does not protect against

## P1 — Application Shell

- [x] U-01 Window chrome: custom title bar, Mica/acrylic policy, dark-first
- [x] U-02 Activity Bar (left rail) with all destinations
- [x] U-03 Primary Sidebar (collapsible, resizable, per-destination content)
- [ ] U-04 Workspace area with tab strip
- [~] U-05 Inspector panel (right, collapsible)
- [x] U-06 Status Bar
- [ ] U-07 Tabs: pin, preview mode, reopen closed, drag-reorder, overflow
- [ ] U-08 Split view: right / down, nested, drag-to-split
- [x] U-09 Command Palette (`Ctrl+Shift+P`)
- [x] U-10 Quick Open (`Ctrl+P`)
- [x] U-11 Quick Capture (`Ctrl+Alt+N`) global hotkey, works minimized
- [~] U-12 Global keyboard shortcut map + customization
- [~] U-13 Context menus (full action set on every object)
- [ ] U-14 Drag & drop from Explorer, between panes, onto destinations
- [~] U-15 Toast / notification surface
- [~] U-16 Empty states, loading states, error states
- [x] U-17 Focus Mode
- [ ] U-18 Study / Presentation Mode with session timer

## P1 — Core Features

- [x] C-01 Home dashboard (today, upcoming, continue, recent, quick capture, print queue)
- [x] C-02 Inbox + triage → convert to any object type
- [ ] C-03 Subjects (CRUD, colors, teachers, per-subject overview)
- [x] C-04 Virtual Collections (query-backed, not folders)
- [ ] C-05 Smart Collections with saved queries
- [x] C-06 Library (textbooks, solved books, references, explanations)
- [x] C-07 Notes (quick, lesson, daily, pinned, scratchpad)
- [x] C-08 Tasks (today/upcoming/overdue/completed/someday, priority, checklists)
- [x] C-09 Assignments (assigned, due, teacher, points, submission, attachments)
- [x] C-10 Requirements (bring-this / prepare-this tracking)
- [x] C-11 Print Center (To Print / Printed / Archive, queue, page counts, drag-in)
- [x] C-12 Links (YouTube / Telegram / web, metadata fetch, thumbnails, collections)
- [ ] C-13 Boards + Threads (Discord-forum-style academic objects)
- [~] C-14 Tags + tag management
- [ ] C-15 Relations + Backlinks (`[[wiki-links]]`)
- [ ] C-16 Academic Profile (About Me, school, year, term)
- [ ] C-17 Goals + progress tracking
- [ ] C-18 Planner (Day / Week / Month / Term) + School Timeline
- [~] C-19 Files tree view (virtual, not disk-mirrored)
- [~] C-20 History per object
- [ ] C-21 Versioning
- [~] C-22 Trash + restore + permanent delete
- [~] C-23 Favorites / pinning
- [ ] C-24 School Year Archive
- [x] C-25 Universal Search (content + metadata + annotations + captions)
- [ ] C-26 Import pipeline (identify → hash → scan → metadata → thumbnail → index → encrypt → store)
- [ ] C-27 Export (original / PDF / selection / collection)
- [ ] C-28 Backup & Recovery (`.campusbackup`, schedules, retention)
- [~] C-29 Settings (all sections, grouped-page style)
- [x] C-30 Offline-first guarantee — zero network dependency for core

## P1 — Viewers & Editors

- [~] V-01 Viewer host + content provider registry
- [ ] V-02 PDF viewer (zoom, fit, thumbnails, outline, search, text selection, rotate, print)
- [ ] V-03 PDF annotations (highlight, draw, comment, persisted encrypted)
- [ ] V-04 Image viewer (PNG/JPEG/WEBP/GIF/BMP/TIFF/HEIC) + crop/rotate/annotate
- [ ] V-05 Video player (MP4/MOV/MKV/WEBM/AVI), speed, PiP, timestamp notes, bookmarks
- [ ] V-06 Audio player + timestamp notes
- [ ] V-07 Markdown editor (split / live preview, tables, checklists, callouts, attachments)
- [ ] V-08 Text/code editor (native, syntax highlighting, find/replace, word wrap)
- [ ] V-09 DOCX preview (Open XML)
- [ ] V-10 PPTX preview + presentation mode
- [ ] V-11 XLSX/CSV viewer (sheets, cells, filters, search)
- [ ] V-12 Unknown file type fallback → Find Extension / Open Externally

## P2 — Extensions

- [ ] X-01 Extension manifest format + permission model
- [ ] X-02 Extension API surface (`campus.*`)
- [ ] X-03 Out-of-process `Campus.PluginHost.exe` + crash isolation
- [ ] X-04 Built-in extensions repackaged as plugins (pdf, images, media, markdown, office, links, print, tasks, notes)
- [ ] X-05 Install from `.campusx` / from folder
- [ ] X-06 Extensions manager UI (Installed / Built-in / From File)
- [ ] X-07 Permission consent dialog
- [ ] X-08 Extension enable/disable/uninstall

## P2 — Services & Platform

- [ ] P-01 `Campus.Service.exe` background host (near-zero idle cost)
- [ ] P-02 `Campus.Indexer.exe` out-of-process text extraction
- [ ] P-03 Startup registration (service only, never UI)
- [ ] P-04 Desktop shortcut + `.campus` file association + shell icon registration
- [x] P-05 Single-instance + deep-link activation

## P2 — Accessibility

- [x] A-01 Full keyboard-only navigation
- [~] A-02 Screen reader names/roles/values on every control
- [~] A-03 UI scaling + independent text scaling
- [ ] A-04 Reduced motion
- [ ] A-05 High contrast
- [x] A-06 Visible focus indicators everywhere
- [ ] A-07 Reading aids: dyslexia-friendly options, line spacing, reading ruler
- [ ] A-08 Large cursor / large hit targets mode
- [~] A-09 Shortcut customization UI
- [x] A-10 Command palette coverage for every action

## P3 — iOS (Campus Pocket) + Sync

- [x] M-01 SwiftUI app skeleton, Apple-native theme parity
- [x] M-02 Quick Capture: Note / Task / Assignment / Requirement / Photo / File / Link
- [x] M-03 Fully offline local Outbox
- [x] M-04 Document scanner (crop, straighten, perspective, → PDF)
- [~] M-05 Share Extension
- [x] M-06 Pairing via QR
- [~] M-07 Local Wi-Fi encrypted sync
- [~] M-08 USB sync path (AFC / documents) with graceful fallback
- [~] M-09 Sync journal + incremental deltas
- [~] M-10 Conflict resolution UI (both sides)
- [x] M-11 CI/CD on GitHub Actions macOS runner → build + archive
- [~] M-12 Repo wired to `github.com/justpainful/Campus`

## P4 — Later

- [ ] L-01 Extension marketplace
- [ ] L-02 Advanced PDF editing
- [ ] L-03 Presentation authoring
- [ ] L-04 Spreadsheet editing
- [ ] L-05 Automation rules

---

## Open decisions / honest constraints

1. **Apple emoji artwork** — the Apple Color Emoji font is proprietary and cannot be redistributed. Campus ships a complete emoji *system* (full Unicode set, every skin-tone sequence, search, ordering, pinning, press-and-hold) with a swappable artwork provider. Point it at an Apple-style asset pack you legally have and it renders identically. Details in `docs/emoji-licensing.md`.
2. **Encryption reality** — an on-disk vault protects files from being read outside Campus. It cannot stop an Administrator on the same machine from reading process memory or screenshotting. Campus states this plainly rather than overpromising.
3. **iOS USB sync** — Apple's AFC/house-arrest path is version-sensitive. Wi-Fi sync is the guaranteed path; USB is an accelerator.
