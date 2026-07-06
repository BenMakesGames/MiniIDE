using System.Collections.Frozen;
using System.Collections.Generic;
using Avalonia.Media;

namespace MiniIde.Models;

public enum ProjectKind { Exe, Lib, Web, Tst }

/// <param name="Glyph">A <see cref="FileIcon"/> glyph constant.</param>
/// <param name="Color">A <see cref="FileIconPalette"/> brush.</param>
/// <param name="IsRunnable">Whether the project can be selected as a startup/run target.</param>
public record ProjectKindInfo(string Glyph, IBrush Color, bool IsRunnable);

public static class ProjectKindExtensions
{
    // Everything except a class library is runnable; one entry per enum member.
    private static readonly FrozenDictionary<ProjectKind, ProjectKindInfo> Info = new Dictionary<ProjectKind, ProjectKindInfo>
    {
        [ProjectKind.Exe] = new(FileIcon.ProjectExe, FileIconPalette.ProjectExe, true),
        [ProjectKind.Lib] = new(FileIcon.ProjectLib, FileIconPalette.ProjectLib, false),
        [ProjectKind.Web] = new(FileIcon.ProjectWeb, FileIconPalette.ProjectWeb, true),
        [ProjectKind.Tst] = new(FileIcon.ProjectTst, FileIconPalette.ProjectTst, true),
    }.ToFrozenDictionary();

    public static ProjectKindInfo GetInfo(this ProjectKind kind) => Info[kind];
}
