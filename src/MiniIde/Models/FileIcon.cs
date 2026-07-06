using System.IO;
using Avalonia.Media;

namespace MiniIde.Models;

// Material Design Icons — Desktop TTF, pinned version 6.8.96.
// Codepoints live in Supplementary Private Use Area-A (U+F0000+).
// C# \uXXXX only reaches U+FFFF; \U000FXXXX is required to encode these.
// If MDI is bumped, re-verify every codepoint against the new release's cheatsheet.html.
public static class FileIcon
{
    public const string CSharp     = "\U000F031B"; // language-csharp
    public const string Json       = "\U000F0626"; // code-json
    public const string Xml        = "\U000F05C0"; // xml
    public const string Csproj     = "\U000F0610"; // microsoft-visual-studio
    public const string Folder     = "\U000F024B"; // folder
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

// NodeKind-level orchestrator: it maps a tree node to its (glyph, color) by dispatching to the
// centralized FileKind / ProjectKind classification. The per-extension and per-project-kind tables
// live in FileKind.cs / ProjectKind.cs — this only decides which classifier a node kind resolves through.
public static class FileIconMap
{
    public static (string Glyph, IBrush Color) From(TreeNode node) => node.Kind switch
    {
        NodeKind.Folder   => (FileIcon.Folder, FileIconPalette.Folder),
        NodeKind.Project  => IconFor(node.ProjectKind ?? Models.ProjectKind.Exe),
        NodeKind.File     => IconFor(Path.GetExtension(node.Path).ToFileKind()),
        NodeKind.Solution => (FileIcon.Folder, FileIconPalette.Folder),
        _                 => (FileIcon.Unknown, FileIconPalette.Unknown),
    };

    private static (string Glyph, IBrush Color) IconFor(FileKind kind)
    {
        var info = kind.GetInfo();
        return (info.Glyph, info.Color);
    }

    private static (string Glyph, IBrush Color) IconFor(ProjectKind kind)
    {
        var info = kind.GetInfo();
        return (info.Glyph, info.Color);
    }
}
