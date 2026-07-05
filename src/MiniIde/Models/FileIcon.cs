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

public static class FileIconMap
{
    public static (string Glyph, IBrush Color) From(TreeNode node) => node.Kind switch
    {
        NodeKind.Folder   => (FileIcon.Folder, FileIconPalette.Folder),
        NodeKind.Project  => FromProjectKind(node.ProjectKind ?? Models.ProjectKind.Exe),
        NodeKind.File     => FromExtension(node.Path),
        NodeKind.Solution => (FileIcon.Folder, FileIconPalette.Folder),
        _                 => (FileIcon.Unknown, FileIconPalette.Unknown),
    };

    private static (string, IBrush) FromProjectKind(ProjectKind kind) => kind switch
    {
        Models.ProjectKind.Exe => (FileIcon.ProjectExe, FileIconPalette.ProjectExe),
        Models.ProjectKind.Lib => (FileIcon.ProjectLib, FileIconPalette.ProjectLib),
        Models.ProjectKind.Web => (FileIcon.ProjectWeb, FileIconPalette.ProjectWeb),
        Models.ProjectKind.Tst => (FileIcon.ProjectTst, FileIconPalette.ProjectTst),
        _                      => (FileIcon.ProjectExe, FileIconPalette.ProjectExe),
    };

    private static (string, IBrush) FromExtension(string? path)
    {
        if (path is null) return (FileIcon.Unknown, FileIconPalette.Unknown);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".cs"                                                                       => (FileIcon.CSharp, FileIconPalette.CSharp),
            ".json"                                                                     => (FileIcon.Json,   FileIconPalette.Json),
            ".csproj"                                                                   => (FileIcon.Csproj, FileIconPalette.Csproj),
            ".xml" or ".axaml" or ".xaml" or ".config" or ".props" or ".targets"        => (FileIcon.Xml,    FileIconPalette.Xml),
            ".txt" or ".md" or ".editorconfig"                                          => (FileIcon.Text,   FileIconPalette.Text),
            ".wav" or ".mp3" or ".ogg" or ".flac"                                       => (FileIcon.Audio,  FileIconPalette.Audio),
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" => (FileIcon.Image,  FileIconPalette.Image),
            ".mp4" or ".mov" or ".avi" or ".webm" or ".mkv"                             => (FileIcon.Video,  FileIconPalette.Video),
            _                                                                           => (FileIcon.Unknown, FileIconPalette.Unknown),
        };
    }
}
