# Windows DirectWrite / Font Subsystem Slowdown — Diagnosis Notes

> **Status:** open problem, **machine-specific** (this desktop only; a laptop with the
> same app is unaffected). Captured while debugging a MiniIDE startup freeze — but the
> root cause is a **Windows font-subsystem fault on this PC**, not an app bug. These notes
> are meant to be portable: take them to Google / a fresh Claude / a Windows forum.

## TL;DR

The **first time a process asks Windows/DirectWrite to build a typeface from a *custom
(non-system) font*, it stalls for a fixed ~13 seconds** on this machine. On a healthy PC
this is effectively instant. The cost is **constant** — it does **not** scale with how many
fonts are installed (proven below). The same DirectWrite path backs File Explorer, which
the user independently reports as sluggish. Strong smell of a **corrupt font file, corrupt
font cache, or a wedged font-related service/timeout** specific to this Windows install.

## Environment

| | |
|---|---|
| OS | Windows 10 Pro, build 10.0.19045 |
| CPU / RAM | Intel Core i7-4790 @ 3.60 GHz / 32 GB |
| .NET | 10.0.204 |
| App framework | Avalonia **12.0.5** (renders text via Skia + HarfBuzz; on Windows the system font manager is `SkFontMgr_DirectWrite`) |
| Installed fonts (after cleanup) | 389 files, **383 MB**, 351 registry entries under `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts` |
| `FontCache` service | Running / Automatic |
| `FontCache3.0.0.0` service | Stopped / Manual (this is the legacy WPF cache — normal, irrelevant) |

## Symptom as first seen

A desktop GUI app (Avalonia) freezes its UI thread for 10–15 s the first time it displays
content that uses an **embedded icon font** (Material Design Icons, a 1.4 MB TTF shipped
inside the app). An empty window with no custom-font text renders instantly. Opening a
document set — which is the first thing that paints icon glyphs — triggers the stall.

## How it was measured

The app's "load documents" logic itself was timed and is **not** the bottleneck:

- Data-model load: **1 ms**
- Full load + populate view models: **82 ms**
- UI thread then stays **busy ~12,900 ms** before going idle (measured by posting a
  `ContextIdle`-priority callback and timing when it fires).

So the freeze is entirely in **first render / text layout**, not in app logic or file I/O.

## The decisive experiment — swap the font, hold everything else constant

Only the icon `FontFamily` was changed between runs. Time = UI-thread busy duration after
data was loaded:

| Font used for the icon glyphs | UI-thread freeze |
|---|---|
| **Embedded custom TTF** (`avares://…MaterialDesignIconsDesktop.ttf#Material Design Icons Desktop`, 1.4 MB) | **~12,900 ms** |
| `Segoe UI` (system default) | 537 ms |
| `Consolas` (installed, non-default system font) | 514 ms |

**Conclusion:** it is specifically the load of a **custom / embedded font** that is slow.
Named system fonts — even non-default ones like Consolas — are fast, so it is *not* "any
non-default family" and *not* glyph fallback for the unusual Private-Use-Area codepoints
(Segoe UI has none of those glyphs yet was fast).

## What was ruled OUT

- **Number/size of installed fonts.** Deleted the Adobe/Kozuka/Noto CJK set: **602 MB → 383 MB**,
  452 → 389 files. Freeze went **12,909 ms → 12,939 ms — i.e. no change at all.** The cost is
  a fixed constant, independent of installed-font volume.
- **Font-cache corruption (first attempt).** Rebuilt the DirectWrite cache
  (`Stop-Service FontCache`; delete `%WINDIR%\ServiceProfiles\LocalService\AppData\Local\FontCache\*`
  and `%WINDIR%\System32\FNTCACHE.DAT`; `Start-Service FontCache`). **No improvement.**
- **Font family-name mismatch.** The embedded TTF's internal family name is exactly
  `Material Design Icons Desktop`, matching the URI. Icons render correctly, so name
  resolution is fine — the cost is not a per-glyph fallback scan from a bad name.
- **App logic** (solution/document load, tree building, view-model wiring): 82 ms total.
- **Fixed, highly reproducible duration** (~12.87–12.94 s across many runs). A *constant*
  multi-second cost that ignores input size smells like a **timeout/retry or a single
  pathological font**, not linear enumeration work.

## Inconclusive / notable

- Pointing the app at a *tiny* (27 KB) embedded test font (`Marlett`) to see whether size
  mattered **crashed the renderer** — but Marlett is a symbol font lacking the requested
  codepoints, so that crash is expected and unrelated. The size question is better answered
  with a proper subset of a normal font (not yet done).

## Best current hypothesis

Avalonia 12 on Windows creates a Skia typeface from the embedded font bytes
(`SKTypeface` from a stream). To support fallback, Skia/DirectWrite must have the **system
font collection** built (`IDWriteFactory::GetSystemFontCollection` /
`SkFontMgr_DirectWrite`). On this machine that first collection build (or a specific font
it touches during the build) **blocks for ~13 s**. Named system fonts hit an already-warm/
cached path and skip the expensive build, which is why they're fast. Because Explorer uses
the same DirectWrite stack, this also explains the reported Explorer sluggishness.

This is inference from black-box timing, not from instrumenting DWrite internals — see
"leads" for how to confirm.

## Corroborating signals

- **Same app is fast on a different PC (laptop).** Points squarely at machine state, not code.
- **File Explorer is independently sluggish on this PC** — same DirectWrite font path.
- The slow operation is **once per process** (a warm font is cached), consistent with a
  one-time system-collection build.

## Leads to chase elsewhere

**Prove where the 13 s goes**
- Capture a **Process Monitor** trace during the stall — look for a font file read that
  hangs/retries, or repeated registry hits under the `Fonts` key.
- Capture an **ETW / Windows Performance Recorder** trace and inspect the stack of the
  hung UI thread; confirm whether it's inside `DWrite!GetSystemFontCollection` or a font
  file parse.
- Minimal repro without the app: a tiny program that calls DirectWrite
  `GetSystemFontCollection(&coll, /*checkForUpdates*/ TRUE)` and times it; or a Skia sample
  that builds `SkFontMgr_DirectWrite` and enumerates families. If that alone takes ~13 s,
  MiniIDE is exonerated completely.

**Likely fixes to try (machine side)**
- `sfc /scannow` then `DISM /Online /Cleanup-Image /RestoreHealth` (repairs corrupt system
  fonts / font state).
- Hunt a **single corrupt font**: bisect `C:\Windows\Fonts` — move half out, retest, repeat.
  A wedged/corrupt font that DWrite retries on would produce exactly this fixed-duration stall.
- Validate fonts with a tool (e.g. FontForge/`ftvalidate`) or Windows'
  **Font settings → "troubleshoot"**; check Event Viewer for font errors during the stall.
- Test under a **brand-new local Windows user profile** and/or **clean-boot** (msconfig,
  disable non-Microsoft services) — rules out a per-profile font install or a shell
  extension / font-manager background app.
- Check for third-party **font manager** software (e.g. a design-tool font syncer) that may
  be intercepting font enumeration.

**Open questions**
- Does the stall reproduce in *any* Avalonia/WPF/Skia app that loads an embedded font, or
  only MiniIDE? (WPF app loading a `pack://…` font would tell us.)
- Is `checkForUpdates=TRUE` on the collection build forcing a slow revalidation because the
  cache keeps getting invalidated? (Would explain why a one-time cache rebuild didn't stick.)

## Minimal repro recipe (for a fresh Claude / forum post)

> Windows 10 (19045). An Avalonia 12 app freezes its UI thread for a *fixed* ~13 s the first
> time it renders text using an **embedded/custom TTF**. Using a system font (Segoe UI,
> Consolas) instead is instant. The freeze is identical before/after deleting 220 MB of
> installed fonts, and survives a FontCache-service cache rebuild. Same app is instant on
> another PC. File Explorer is also sluggish on the affected PC. What in the DirectWrite /
> Windows font subsystem causes a constant ~13 s cost to build a custom-font typeface (or
> the system font collection), and how do I find the offending font/service?

---
*Generated by Claude Code while debugging MiniIDE. This file is scratch notes about the PC,
not part of the MiniIDE project — move or delete it freely.*
