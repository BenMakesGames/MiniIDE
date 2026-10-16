namespace MiniIde.Models;

// Material Design Icons — Desktop TTF, pinned version 6.8.96.
// Codepoints live in Supplementary Private Use Area-A (U+F0000+).
// C# \uXXXX only reaches U+FFFF; \U000FXXXX is required to encode these.
// If MDI is bumped, re-verify every codepoint against the new release's cheatsheet.html.
//
// Menu-action glyphs (Reload / Claude / Explorer / external open / project actions). Kept separate from FileIcon,
// which is scoped to file-type and project-kind glyphs — action glyphs don't belong there.
//
// ── Added or changed a glyph below? Re-run the subset script: ─────────────────
//        cd tools && npm run subset
// The shipped font is a tiny subset of ONLY the glyphs referenced in source (the full
// MDI font takes ~12-40s to load because of its ligature table). Skip the re-run and your
// new icon renders as a blank box — the MiniIde.Tests coverage test fails loudly if you do.
public static class ActionIcon
{
    public const string Reload       = "\U000F0450"; // refresh
    public const string Claude       = "\U000F06A9"; // robot
    public const string Explorer     = "\U000F024B"; // folder
    public const string Terminal     = "\U000F018D"; // console-line
    public const string OpenExternal = "\U000F03CC"; // open-in-new
    public const string Run          = "\U000F040A"; // play
    public const string GitPull      = "\U000F0045"; // arrow-down
    public const string GitPush      = "\U000F005D"; // arrow-up
    public const string FindProblems = "\U000F05D6"; // alert-circle-outline
    public const string NuGet        = "\U000F03D6"; // package-variant
    public const string Star         = "\U000F04CE"; // star
    public const string StarOutline  = "\U000F04D2"; // star-outline
}
