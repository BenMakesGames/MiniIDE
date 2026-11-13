
namespace MiniIde.Models;

// Material Design Icons — Desktop TTF, pinned version 6.8.96.
// Codepoints live in Supplementary Private Use Area-A (U+F0000+).
// C# \uXXXX only reaches U+FFFF; \U000FXXXX is required to encode these.
// If MDI is bumped, re-verify every codepoint against the new release's cheatsheet.html.
//
// Added or changed a glyph below? Re-run the subset script (cd tools && npm run subset) —
// the shipped font ships only the glyphs referenced in source. See ActionIcon for the why.
public static class ProjectIcon
{
    public const string Exe = "\U000F08C6"; // application
    public const string Lib = "\U000F1177"; // file-link
    public const string Web = "\U000F059F"; // web
    public const string Tst = "\U000F0668"; // test-tube
}
