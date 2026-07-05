# Material Design Icons (Desktop)

- **Pinned version**: 6.8.96
- **Source**: https://github.com/Templarian/MaterialDesign-Font
- **License**: Apache 2.0 (fonts) — see `LICENSE` in this folder.

## Font family name

The `#` suffix in the Avalonia `avares://` URI must be the TTF's internal Family Name:

    Material Design Icons Desktop

Do not change without re-verifying with Windows Font Viewer / a font inspector.

## Updating

1. Replace `MaterialDesignIconsDesktop.ttf` with the newer release from the source repo.
2. Refresh `LICENSE` from the same tag.
3. Update the version above and the version comment atop `Models/FileIcon.cs`.
4. Re-verify every codepoint in `Models/FileIcon.cs` against the new release's `cheatsheet.html` — MDI has renumbered on major bumps historically.
