namespace MiniIde.Models;

// Material Design Icons — Desktop TTF, pinned version 6.8.96.
// Codepoints live in Supplementary Private Use Area-A (U+F0000+).
// C# \uXXXX only reaches U+FFFF; \U000FXXXX is required to encode these.
// If MDI is bumped, re-verify every codepoint against the new release's cheatsheet.html.
//
// Menu-action glyphs (Reload / Claude / Explorer). Kept separate from FileIcon,
// which is scoped to file-type tree glyphs — action glyphs don't belong there.
public static class ActionIcon
{
    public const string Reload   = "\U000F0450"; // refresh
    public const string Claude   = "\U000F06A9"; // robot
    public const string Explorer = "\U000F024B"; // folder
}
