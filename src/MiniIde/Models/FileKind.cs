using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Avalonia.Media;

namespace MiniIde.Models;

// The single source of truth for "what kind of file is this". An extension maps to exactly one
// FileKind; each FileKind carries its icon glyph, color, highlight mode, and tab-kind in one record.
// Every consumer (tree icons, editor highlight, tab dispatch, startup-solution recognition) derives
// from here — adding a file type is a one-line edit to the two tables below.
public enum FileKind
{
    CSharp,
    Json,
    Xml,
    Csproj,
    Solution,
    Image,
    VectorImage,
    Audio,
    Video,
    Text,
    Unknown,
}

/// <param name="Glyph">A <see cref="FileIcon"/> glyph constant.</param>
/// <param name="Color">A <see cref="FileIconPalette"/> brush.</param>
/// <param name="Highlight">Syntax-highlight mode when opened in an editor tab.</param>
/// <param name="OpensAsImageTab">Whether the file opens in an image preview tab (vs. an editor tab).</param>
public record FileKindInfo(string Glyph, IBrush Color, HighlightMode Highlight, bool OpensAsImageTab);

public static class FileKindExtensions
{
    // One entry per enum member — a missing entry would be a latent KeyNotFoundException in GetInfo.
    private static readonly FrozenDictionary<FileKind, FileKindInfo> Info = new Dictionary<FileKind, FileKindInfo>
    {
        [FileKind.CSharp]      = new(FileIcon.CSharp,  FileIconPalette.CSharp,  HighlightMode.CSharp, false),
        [FileKind.Json]        = new(FileIcon.Json,    FileIconPalette.Json,    HighlightMode.Json,   false),
        [FileKind.Xml]         = new(FileIcon.Xml,     FileIconPalette.Xml,     HighlightMode.Xml,    false),
        [FileKind.Csproj]      = new(FileIcon.Csproj,  FileIconPalette.Csproj,  HighlightMode.Xml,    false),
        // .sln/.slnx share the Visual Studio glyph; Highlight=Xml is .slnx-correct and cosmetic for legacy .sln.
        [FileKind.Solution]    = new(FileIcon.Csproj,  FileIconPalette.Csproj,  HighlightMode.Xml,    false),
        [FileKind.Image]       = new(FileIcon.Image,   FileIconPalette.Image,   HighlightMode.None,   true),
        // .svg/.ico show the image icon but open as text: Skia can't decode ICO and SVG needs Avalonia.Svg.Skia.
        [FileKind.VectorImage] = new(FileIcon.Image,   FileIconPalette.Image,   HighlightMode.None,   false),
        [FileKind.Audio]       = new(FileIcon.Audio,   FileIconPalette.Audio,   HighlightMode.None,   false),
        [FileKind.Video]       = new(FileIcon.Video,   FileIconPalette.Video,   HighlightMode.None,   false),
        [FileKind.Text]        = new(FileIcon.Text,    FileIconPalette.Text,    HighlightMode.None,   false),
        [FileKind.Unknown]     = new(FileIcon.Unknown, FileIconPalette.Unknown, HighlightMode.None,   false),
    }.ToFrozenDictionary();

    // Case-insensitive so .PNG, .CS, etc. classify the same as their lowercase forms.
    private static readonly FrozenDictionary<string, FileKind> ByExtension = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"]           = FileKind.CSharp,
        [".json"]         = FileKind.Json,
        [".xml"]          = FileKind.Xml,
        [".axaml"]        = FileKind.Xml,
        [".xaml"]         = FileKind.Xml,
        [".config"]       = FileKind.Xml,
        [".props"]        = FileKind.Xml,
        [".targets"]      = FileKind.Xml,
        [".csproj"]       = FileKind.Csproj,
        [".slnx"]         = FileKind.Solution,
        [".sln"]          = FileKind.Solution,
        [".png"]          = FileKind.Image,
        [".jpg"]          = FileKind.Image,
        [".jpeg"]         = FileKind.Image,
        [".bmp"]          = FileKind.Image,
        [".webp"]         = FileKind.Image,
        [".gif"]          = FileKind.Image,
        [".svg"]          = FileKind.VectorImage,
        [".ico"]          = FileKind.VectorImage,
        [".wav"]          = FileKind.Audio,
        [".mp3"]          = FileKind.Audio,
        [".ogg"]          = FileKind.Audio,
        [".flac"]         = FileKind.Audio,
        [".mp4"]          = FileKind.Video,
        [".mov"]          = FileKind.Video,
        [".avi"]          = FileKind.Video,
        [".webm"]         = FileKind.Video,
        [".mkv"]          = FileKind.Video,
        [".txt"]          = FileKind.Text,
        [".md"]           = FileKind.Text,
        [".editorconfig"] = FileKind.Text,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static FileKindInfo GetInfo(this FileKind kind) => Info[kind];

    /// <summary>
    /// Maps a bare extension (e.g. from <see cref="System.IO.Path.GetExtension(string?)"/>, with or without
    /// a leading dot, case-insensitive) to its <see cref="FileKind"/>, falling back to <see cref="FileKind.Unknown"/>.
    /// </summary>
    public static FileKind ToFileKind(this string? extension) =>
        extension is not null && ByExtension.TryGetValue(extension, out var kind) ? kind : FileKind.Unknown;
}
