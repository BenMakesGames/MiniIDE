# Icon-font tooling

The app renders icons with a Material Design Icons font (`src/MiniIde/Assets/icons/MaterialDesignIconsDesktop.ttf`).
The full MDI font is ~1.4 MB and takes **~12–40 s to load** on first use because of its ~6,900-entry
ligature `GSUB` table. We don't use ligatures (glyphs are addressed by codepoint), so the shipped
font is a **subset of only the glyphs referenced in source** — a few KB that loads in ~10 ms.

## Regenerate the shipped subset

Run this whenever you add or change an icon codepoint in `src/MiniIde` (e.g. `ActionIcon`, `FileIcon`):

```
cd tools
npm install     # first time only
npm run subset
```

`subset-icons.mjs` scans `src/MiniIde/**/*.cs` for `\U000FXXXX` escapes, then rebuilds
`MaterialDesignIconsDesktop.ttf` from just those glyphs (dropping `GSUB`/`GPOS`/`GDEF`).

## Guard

`src/MiniIde.Tests` has a test that fails if any referenced glyph is missing from the shipped font —
so a forgotten `npm run subset` is caught by `dotnet test` rather than shipping a blank-box icon.

## Files

- `fonts/MaterialDesignIconsDesktop.full.ttf` — the full MDI source font (pinned 6.8.96). **Not shipped**; only the subset is. To bump MDI, replace this and re-run.
- `subset-icons.mjs` — the generator.
- `package.json` / `package-lock.json` — committed; `node_modules/` is git-ignored.
