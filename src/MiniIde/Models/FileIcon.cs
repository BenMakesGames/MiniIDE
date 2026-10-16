
namespace MiniIde.Models;

// Material Design Icons — Desktop TTF, pinned version 6.8.96.
// Codepoints live in Supplementary Private Use Area-A (U+F0000+).
// C# \uXXXX only reaches U+FFFF; \U000FXXXX is required to encode these.
// If MDI is bumped, re-verify every codepoint against the new release's cheatsheet.html.
//
// Added or changed a glyph below? Re-run the subset script (cd tools && npm run subset) —
// the shipped font ships only the glyphs referenced in source. See ActionIcon for the why.
public static class FileIcon
{
    public const string CSharp     = "\U000F031B"; // language-csharp
    public const string Json       = "\U000F0626"; // code-json
    public const string Xml        = "\U000F05C0"; // xml
    public const string Csproj     = "\U000F0610"; // microsoft-visual-studio
    public const string Text       = "\U000F0219"; // file-document
    public const string Audio      = "\U000F0387"; // music-note
    public const string Image      = "\U000F021F"; // file-image
    public const string Video      = "\U000F022B"; // file-video
    public const string ProjectExe = "\U000F08C6"; // application
    public const string ProjectLib = "\U000F1177"; // file-link
    public const string ProjectWeb = "\U000F059F"; // web
    public const string ProjectTst = "\U000F0668"; // test-tube
    public const string Unknown    = "\U000F0224"; // file-outline
}
